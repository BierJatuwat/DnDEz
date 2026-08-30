using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 2b (security fix, Critical) — ปิดช่องโหว่ตระกูล **CVE-2018-1058**
/// (`SECURITY DEFINER` + `search_path` ที่ไม่ได้ pin + สิทธิ์ `TEMP`) ซึ่งเป็น "กุญแจผี" ของ RLS ทั้งระบบ
///
/// **ช่องโหว่ (พิสูจน์จริงบน DB dev แล้ว ไม่ใช่สมมติฐาน)** — ฟังก์ชัน `app_*` **ที่แตะตาราง** อ้างชื่อ
/// แบบไม่ qualify (`FROM campaign_members`, `FROM campaigns`, `FROM api_keys`) และไม่มี `search_path`
/// ที่ pin ไว้เลยสักตัว (`pg_proc.proconfig IS NULL` ทั้ง 8) · Postgres ค้น `pg_temp` **ก่อน** ทุก schema เสมอ
/// ผู้ใช้ที่ล็อกอินแล้วจึงทำแบบนี้ได้:
/// <code>
/// CREATE TEMP TABLE campaign_members (campaign_id uuid, user_id uuid, role text);
/// INSERT INTO campaign_members VALUES ('&lt;โต๊ะไหนก็ได้&gt;', '&lt;ตัวเอง&gt;', 'Dm');
/// </code>
/// แล้ว `app_is_dm()` จะ resolve ไปที่ตารางปลอมของผู้โจมตีแทน `public.campaign_members` →
/// **ผู้เล่นธรรมดากลายเป็น DM ของทุกโต๊ะในสายตา RLS** ไม่ใช่แค่ตารางเดียว แต่ทุก policy ในระบบ
///
/// ── สองข้อเท็จจริงที่พิสูจน์ด้วยการทดลองจริง และเป็นเหตุผลที่ fix ต้องเขียนแบบนี้เป๊ะ ─────────────
///
///  1. ⚠️ **`REVOKE TEMP ... FROM dndez_app` เป็น no-op เงียบ ๆ** — `pg_database.datacl` ของ `dndez`
///     เป็น NULL (ACL ดีฟอลต์) แปลว่าสิทธิ์ TEMP มาจาก **PUBLIC** ไม่ได้ grant ตรงให้ role
///     คำสั่ง REVOKE จาก role จึงคืน `REVOKE` สำเร็จโดยไม่ error แต่ `has_database_privilege` ยังเป็น
///     true อยู่ → ต้อง `REVOKE TEMP ON DATABASE dndez FROM PUBLIC` เท่านั้น
///     (ระวัง: PUBLIC ถือทั้ง `T` และ `c` — ห้ามเผลอ revoke CONNECT ไปด้วย ไม่งั้น API ต่อ DB ไม่ได้เลย)
///
///  2. ⚠️ **การ "ไม่ใส่ pg_temp" ใน search_path ไม่ได้แปลว่าตัดมันออก** — ผลการทดลอง:
///     <list type="bullet">
///       <item>ไม่ตั้ง search_path เลย → โดน shadow</item>
///       <item>`SET search_path = public` (ละ pg_temp) → **ยังโดน shadow**</item>
///       <item>`SET search_path = pg_catalog, public, pg_temp` (pg_temp ท้ายสุด) → ปลอดภัย</item>
///       <item>`SET search_path = pg_temp, public` → โดน shadow</item>
///     </list>
///     Postgres แทรก `pg_temp` ไว้ **หน้าสุด** โดยปริยายถ้าไม่ได้ระบุตำแหน่งมันเอง วิธีลดอันดับ
///     `pg_temp` จึงมีทางเดียวคือ **ระบุมันไว้ท้ายสุดอย่างชัดเจน** (ตรงกับที่ Postgres manual แนะนำ)
///     เขียนเป็น `SET search_path = public` เฉย ๆ จะได้ "ความมั่นใจปลอม" ซึ่งแย่กว่าไม่แก้เลย
///
/// เทสต์นี้ยิงตรงที่ role `dndez_app` ผ่าน raw SQL โดยตั้งใจ (ไม่ผ่าน HTTP) — จุดประสงค์คือพิสูจน์ชั้น DB
/// ล้วน ๆ เหมือน <c>AutonomySettingRlsTests</c> · ใช้ `Pooling=false` ทุก connection เพราะ temp object
/// ผูกกับ session ถ้าปล่อยให้ pool เอา connection ที่มีตารางปลอมกลับไปใช้ต่อ เทสต์ตัวอื่นจะเพี้ยนตาม
/// </summary>
public class SecurityDefinerSearchPathTests : IAsyncLifetime
{
    // owner role — seed/cleanup เท่านั้น (bypass RLS ตามธรรมชาติของ owner)
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    // role runtime จริงของ API · Pooling=false = การันตีว่า temp object ไม่รั่วข้ามเทสต์
    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app;Pooling=false";

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _dmUserId = Guid.NewGuid();
    private readonly Guid _playerUserId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (id, label) in new[] { (_dmUserId, "dm"), (_playerUserId, "player") })
        {
            await using var insertUser = new NpgsqlCommand(
                """
                INSERT INTO users (id, email, display_name, password_hash, created_at)
                VALUES (@id, @email, @label, 'not-a-real-hash', now())
                """, conn);
            insertUser.Parameters.AddWithValue("id", id);
            insertUser.Parameters.AddWithValue("email", $"searchpath-{label}-{id}@example.test");
            insertUser.Parameters.AddWithValue("label", label);
            await insertUser.ExecuteNonQueryAsync();
        }

        await using (var insertCampaign = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, 'search_path fixture', 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn))
        {
            insertCampaign.Parameters.AddWithValue("id", _campaignId);
            insertCampaign.Parameters.AddWithValue("ownerId", _dmUserId);
            await insertCampaign.ExecuteNonQueryAsync();
        }

        foreach (var (userId, role) in new[] { (_dmUserId, "Dm"), (_playerUserId, "Player") })
        {
            await using var insertMember = new NpgsqlCommand(
                """
                INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
                VALUES (@id, @campaignId, @userId, @role, now())
                """, conn);
            insertMember.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMember.Parameters.AddWithValue("campaignId", _campaignId);
            insertMember.Parameters.AddWithValue("userId", userId);
            insertMember.Parameters.AddWithValue("role", role);
            await insertMember.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaign = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = @id", conn))
        {
            deleteCampaign.Parameters.AddWithValue("id", _campaignId);
            await deleteCampaign.ExecuteNonQueryAsync();
        }

        await using var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn);
        deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId });
        await deleteUsers.ExecuteNonQueryAsync();
    }

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAsAsync(Guid userId)
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand("SELECT set_config('app.user_id', @v, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("v", userId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var setCampaign = new NpgsqlCommand("SELECT set_config('app.campaign_id', @v, true)", conn, tx))
        {
            setCampaign.Parameters.AddWithValue("v", _campaignId.ToString());
            await setCampaign.ExecuteScalarAsync();
        }

        return (conn, tx);
    }

    // ── ชั้นที่ 1: ตัด attack surface ทิ้ง (REVOKE TEMP) ──────────────────

    [Fact]
    public async Task Dndez_app_has_no_TEMP_privilege_on_the_database()
    {
        // ต้องเช็คผ่าน has_database_privilege ไม่ใช่แค่ดูว่า REVOKE รันผ่าน — เพราะ REVOKE จาก role
        // (แทนที่จะเป็นจาก PUBLIC) สำเร็จเงียบ ๆ โดยไม่มีผลอะไรเลย
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT has_database_privilege('dndez_app', 'dndez', 'TEMP')", conn);
        Assert.False((bool)(await cmd.ExecuteScalarAsync())!,
            "dndez_app ยังมีสิทธิ์ TEMP — shadow temp table attack ยังทำได้อยู่");
    }

    [Fact]
    public async Task Dndez_app_still_has_CONNECT_privilege()
    {
        // ตาข่ายกันพลาด: PUBLIC ถือทั้ง T และ c ในสตริง ACL เดียวกัน ถ้าใครเผลอ
        // `REVOKE ALL ... FROM PUBLIC` แทนที่จะ revoke แค่ TEMP ทั้งแอปจะต่อ DB ไม่ได้เลย
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT has_database_privilege('dndez_app', 'dndez', 'CONNECT')", conn);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!,
            "dndez_app ต้องต่อ DB ได้ตามปกติ — revoke แรงเกินไป");
    }

    [Fact]
    public async Task Dndez_app_cannot_create_a_temp_table()
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "CREATE TEMP TABLE campaign_members (campaign_id uuid, user_id uuid, role text)", conn);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    [Fact]
    public async Task Dndez_app_cannot_create_a_temp_function()
    {
        // ตัวแปรที่คนมักลืม: CVE-2018-1058 ครอบ **ฟังก์ชัน** ด้วยไม่ใช่แค่ตาราง — pg_temp.app_current_user_id()
        // จะ shadow ตัวจริงที่ถูกเรียกอยู่ข้างใน app_is_dm() ได้เหมือนกัน
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "CREATE FUNCTION pg_temp.app_current_user_id() RETURNS uuid LANGUAGE sql AS $$ SELECT gen_random_uuid() $$",
            conn);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    // ── ชั้นที่ 2: pin search_path (defense in depth) ─────────────────────

    [Theory]
    [InlineData("app_is_dm")]
    [InlineData("app_is_member")]
    [InlineData("app_is_member_of")]
    [InlineData("app_current_campaign")]
    [InlineData("app_current_user_id")]
    [InlineData("app_create_campaign")]
    [InlineData("app_get_dm_api_key")]
    [InlineData("app_join_campaign")]
    public async Task App_function_has_search_path_pinned_with_pg_temp_last(string functionName)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // ต้อง filter schema ด้วย และต้องยืนยันว่าเจอ "แถวเดียว" — ถ้าวันหลังมีคนเพิ่ม overload
        // (เช่น app_is_member_of(uuid, text)) แล้ว pin ไม่ครบทุกตัว การอ่านแค่แถวแรกจะผ่านเงียบ ๆ
        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.proconfig
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.proname = @name
            """, conn);
        cmd.Parameters.AddWithValue("name", functionName);

        var proconfigs = new List<string[]?>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                proconfigs.Add(reader.IsDBNull(0) ? null : reader.GetFieldValue<string[]>(0));
            }
        }

        var proconfig = Assert.Single(proconfigs);

        Assert.NotNull(proconfig);
        var setting = Assert.Single(proconfig!, s => s.StartsWith("search_path=", StringComparison.Ordinal));

        var schemas = setting["search_path=".Length..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("pg_temp", schemas);

        // หัวใจของ fix — pg_temp ต้องอยู่ "ท้ายสุด" ไม่ใช่แค่ "มีอยู่" และไม่ใช่ "ไม่ใส่"
        // ถ้าละ pg_temp ทิ้ง Postgres จะแทรกมันกลับมาไว้หน้าสุดเอง = ยังโดน hijack เหมือนเดิม
        Assert.Equal("pg_temp", schemas[^1]);
    }

    [Fact]
    public async Task No_security_definer_function_anywhere_is_left_without_pg_temp_last()
    {
        // ตาข่ายกันอนาคต — เทสต์ [Theory] ข้างบน hardcode ชื่อฟังก์ชัน 8 ตัวที่มี "วันนี้" ถ้าพรุ่งนี้มีคน
        // เพิ่มฟังก์ชัน SECURITY DEFINER ตัวใหม่โดยลืม pin search_path มันจะรอดสายตาไปเงียบ ๆ
        // เทสต์นี้จึงถามจาก pg_proc ตรง ๆ ว่า "มีตัวไหนหลุดบ้าง" แทนที่จะไล่เช็คตามรายชื่อ
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // ประชากรที่ตรวจ = `prosecdef` **หรือ** ชื่อขึ้นต้น `app_` โดยตั้งใจ · ตัวแรกจับ "ฟังก์ชัน
        // SECURITY DEFINER ตัวใหม่ชื่ออะไรก็ตาม" ตัวหลังจับ "helper app_* ตัวใหม่ที่ไม่ใช่ SECURITY
        // DEFINER แต่ถูกเรียกจากข้างในตัวที่ใช่" ซึ่งเป็นเหตุผลเดียวกับข้อ 6 ของ doc comment ใน migration
        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.oid::regprocedure::text,
                   COALESCE(
                       (SELECT cfg FROM unnest(p.proconfig) AS cfg WHERE cfg LIKE 'search_path=%'),
                       ''
                   ) AS sp
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public'
              AND (p.prosecdef OR p.proname LIKE 'app\_%')
            ORDER BY 1
            """, conn);

        var unpinned = new List<string>();
        var inspected = 0;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                inspected++;
                var schemas = reader.GetString(1)
                    .Replace("search_path=", string.Empty)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                if (schemas.Length == 0 || schemas[^1] != "pg_temp")
                {
                    unpinned.Add(reader.GetString(0));
                }
            }
        }

        // กันเทสต์ผ่านแบบว่างเปล่า: ถ้า query เพี้ยนจนไม่เจอฟังก์ชันเลย ต้องดังขึ้นมา ไม่ใช่เขียวเงียบ ๆ
        Assert.True(inspected >= 8, $"คาดว่าจะเจอฟังก์ชันอย่างน้อย 8 ตัว แต่เจอ {inspected} — query อาจ scope ผิด");

        Assert.True(unpinned.Count == 0,
            "ฟังก์ชัน SECURITY DEFINER ต่อไปนี้ยังไม่ได้ pin search_path ให้ pg_temp อยู่ท้ายสุด " +
            $"(CVE-2018-1058): {string.Join(", ", unpinned)}");
    }

    // ── การโจมตีจริง: ต้องตายทั้งดุ้น ─────────────────────────────────────

    [Fact]
    public async Task Player_cannot_escalate_to_dm_via_shadow_temp_table()
    {
        // นี่คือ PoC ตัวเต็มที่เคยสำเร็จมาแล้วบน DB dev — ผู้เล่นธรรมดายกตัวเองเป็น DM แล้วเขียน
        // autonomy_settings (ตาราง DM-only) ได้จริง
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var beforeCmd = new NpgsqlCommand("SELECT app_is_dm()", conn, tx))
        {
            Assert.False((bool)(await beforeCmd.ExecuteScalarAsync())!, "ผู้เล่นไม่ควรเป็น DM ตั้งแต่ต้น");
        }

        // ด่านที่ 1 ต้องหยุดตรงนี้เลย
        await using (var shadow = new NpgsqlCommand(
            "CREATE TEMP TABLE campaign_members (campaign_id uuid, user_id uuid, role text)", conn, tx))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => shadow.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Shadow_temp_table_cannot_flip_app_is_dm_even_if_temp_were_creatable()
    {
        // ชั้นที่ 2 แยกเดี่ยว: สมมติวันหลังมีคนคืนสิทธิ์ TEMP ให้ (เช่นตั้ง extension บางตัว) การ pin
        // search_path ต้องยังกันไว้ได้เอง · ที่นี่ใช้ owner role (superuser ข้ามเช็คสิทธิ์ TEMP ได้)
        // สร้างตารางปลอมจริง ๆ แล้วยืนยันว่า app_is_dm() ยังอ่านจาก public.campaign_members
        await using var conn = new NpgsqlConnection(
            "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez;Pooling=false");
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand("SELECT set_config('app.user_id', @v, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("v", _playerUserId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var setCampaign = new NpgsqlCommand("SELECT set_config('app.campaign_id', @v, true)", conn, tx))
        {
            setCampaign.Parameters.AddWithValue("v", _campaignId.ToString());
            await setCampaign.ExecuteScalarAsync();
        }

        await using (var shadow = new NpgsqlCommand(
            "CREATE TEMP TABLE campaign_members (campaign_id uuid, user_id uuid, role text)", conn, tx))
        {
            await shadow.ExecuteNonQueryAsync();
        }

        await using (var poison = new NpgsqlCommand(
            "INSERT INTO pg_temp.campaign_members VALUES (@c, @u, 'Dm')", conn, tx))
        {
            poison.Parameters.AddWithValue("c", _campaignId);
            poison.Parameters.AddWithValue("u", _playerUserId);
            await poison.ExecuteNonQueryAsync();
        }

        await using (var isDm = new NpgsqlCommand("SELECT app_is_dm()", conn, tx))
        {
            Assert.False((bool)(await isDm.ExecuteScalarAsync())!,
                "app_is_dm() อ่านจากตารางปลอมใน pg_temp — search_path ยังไม่ถูก pin ให้ pg_temp อยู่ท้ายสุด");
        }

        await tx.RollbackAsync();
    }

    // ── regression: ของเดิมต้องยังทำงานถูกต้อง ────────────────────────────

    [Fact]
    public async Task Legitimate_dm_is_still_recognised_after_the_fix()
    {
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var isDm = new NpgsqlCommand("SELECT app_is_dm()", conn, tx))
        {
            Assert.True((bool)(await isDm.ExecuteScalarAsync())!, "DM ตัวจริงต้องยังเป็น DM");
        }

        await using (var isMember = new NpgsqlCommand("SELECT app_is_member()", conn, tx))
        {
            Assert.True((bool)(await isMember.ExecuteScalarAsync())!);
        }

        await using (var isMemberOf = new NpgsqlCommand("SELECT app_is_member_of(@c)", conn, tx))
        {
            isMemberOf.Parameters.AddWithValue("c", _campaignId);
            Assert.True((bool)(await isMemberOf.ExecuteScalarAsync())!);
        }

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Legitimate_player_is_member_but_not_dm_after_the_fix()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var isMember = new NpgsqlCommand("SELECT app_is_member()", conn, tx))
        {
            Assert.True((bool)(await isMember.ExecuteScalarAsync())!, "ผู้เล่นต้องยังเป็นสมาชิก");
        }

        await using (var isDm = new NpgsqlCommand("SELECT app_is_dm()", conn, tx))
        {
            Assert.False((bool)(await isDm.ExecuteScalarAsync())!);
        }

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Session_settings_helpers_still_resolve_correctly_after_the_fix()
    {
        // app_current_user_id()/app_current_campaign() ถูก pin ด้วย ต้องแน่ใจว่า current_setting()
        // ยัง resolve ได้ตามปกติหลังบังคับ search_path (pg_catalog ต้องยังอยู่ในสายตา)
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var cmd = new NpgsqlCommand("SELECT app_current_user_id()", conn, tx))
        {
            Assert.Equal(_dmUserId, (Guid)(await cmd.ExecuteScalarAsync())!);
        }

        await using (var cmd = new NpgsqlCommand("SELECT app_current_campaign()", conn, tx))
        {
            Assert.Equal(_campaignId, (Guid)(await cmd.ExecuteScalarAsync())!);
        }

        await tx.RollbackAsync();
    }
}

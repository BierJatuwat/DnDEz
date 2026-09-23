using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Part D ของแผน "DM AI Assistant" (2026-09-22) — `campaign_journal_entries` คือ **ชั้นรายละเอียด
/// ที่อยู่ระหว่าง log ดิบกับ `recap_snapshots` ที่สรุปสั้น** เปิดกว้างว่าใครเขียนก็ได้ (DM พิมพ์เอง
/// หรือ AI สรุปให้ — ดูคอลัมน์ `authored_by`)
///
/// ## ทำไมต้อง DM-only ที่ชั้น DB ไม่ใช่แค่เช็คใน C#
/// เนื้อหาที่ไหลลงตารางนี้มาจากเส้นทางเดียวกับ `consult_messages` ของ Part C คือบทสนทนาที่ AI เห็น
/// `StoryBeat.DmNotes`/`Secrets` ของ DM — บันทึกจึงอ้างถึงความลับของแคมเปญได้ตรง ๆ **ผู้เล่นต้องอ่าน
/// ไม่ได้แม้แต่แถวเดียว** · ใช้ `dm_only FOR ALL` ตัวเดียวเหมือน `consult_messages` (การที่ `FOR ALL`
/// เอา `USING` ไปคุม SELECT ด้วยคือผลที่ต้องการพอดี) **ไม่ใช่** `member_read` + `dm_write` แบบ
/// `autonomy_settings` ที่ผู้เล่นควรอ่านได้
///
/// ## ⚠️ เทสต์ `Table_has_row_level_security_enabled_and_forced` สำคัญกว่าที่เห็น
/// ตารางใหม่ในโปรเจกต์นี้ **fail OPEN** — ไม่มี auto-inherit RLS จาก migration เก่า (loop ใน
/// migration `RowLevelSecurity` รันครั้งเดียวตอนนั้น ไม่ใช่ trigger ถาวร) ถ้าลืม `ENABLE`/`FORCE`
/// เทสต์ policy logic ด้านล่างจะ**ผ่านหมดโดยไม่มีอะไรกั้นจริงเลย** (ไม่มี policy ให้ทดสอบ ≠ ปลอดภัย)
///
/// ยิงตรงที่ role `dndez_app` ผ่าน raw SQL โดยตั้งใจ (ไม่ผ่าน HTTP) — พิสูจน์ชั้น DB ล้วน ๆ ว่ากันได้เอง
/// แม้เช็คใน C# หายไป · contract ระดับ endpoint อยู่ใน <c>CampaignJournalEndpointsTests.cs</c>
/// </summary>
public class CampaignJournalRlsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _dmUserId = Guid.NewGuid();
    private readonly Guid _playerUserId = Guid.NewGuid();
    private readonly Guid _existingEntryId = Guid.NewGuid();

    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherDmUserId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (id, label) in new[]
                 {
                     (_dmUserId, "dm"), (_playerUserId, "player"), (_otherDmUserId, "other-dm"),
                 })
        {
            await ExecAsync(conn,
                """
                INSERT INTO users (id, email, display_name, password_hash, created_at)
                VALUES (@id, @email, @label, 'not-a-real-hash', now())
                """,
                ("id", id), ("email", $"journal-rls-{label}-{id}@example.test"), ("label", label));
        }

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, "Journal RLS fixture");
        await InsertCampaignAsync(conn, _otherCampaignId, _otherDmUserId, "Journal RLS fixture (other campaign)");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _otherDmUserId, "Dm");

        // แถวที่มีอยู่จริง — ถ้าผู้เล่น SELECT แล้วได้ 0 แปลว่า RLS กันจริง ไม่ใช่ตารางว่างเปล่า
        await ExecAsync(conn,
            """
            INSERT INTO campaign_journal_entries (id, campaign_id, session_id, title, content, authored_by, created_at)
            VALUES (@id, @campaignId, NULL, 'คืนที่วายร้ายเปิดตัว', 'ชื่อจริงของวายร้ายคือลอร์ดเวลรอธ', 'Dm', now())
            """,
            ("id", _existingEntryId), ("campaignId", _campaignId));
    }

    // ── ตัวตาราง: ต้องเปิด RLS เองจริง (ไม่ใช่เชื่อว่า inherit มา) ─────────────

    [Fact]
    public async Task Table_has_row_level_security_enabled_and_forced()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT relrowsecurity, relforcerowsecurity
            FROM pg_class
            WHERE oid = 'public.campaign_journal_entries'::regclass
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ไม่พบตาราง campaign_journal_entries");
        Assert.True(reader.GetBoolean(0), "campaign_journal_entries: ENABLE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
        Assert.True(reader.GetBoolean(1), "campaign_journal_entries: FORCE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
    }

    [Fact]
    public async Task Table_has_a_dm_only_policy_not_a_member_wide_one()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT policyname, qual FROM pg_policies WHERE tablename = 'campaign_journal_entries'", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var policies = new List<(string Name, string Qual)>();
        while (await reader.ReadAsync())
        {
            policies.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        Assert.NotEmpty(policies);
        // ทุก policy ต้องเรียก app_is_dm() — ถ้ามีตัวไหนเหลือแค่ app_is_member() แปลว่าผู้เล่นอ่านได้
        Assert.All(policies, p => Assert.Contains("app_is_dm", p.Qual));
    }

    // ── SELECT: DM เท่านั้น ───────────────────────────────────────────────────

    [Fact]
    public async Task Dm_can_read_own_campaign_journal_at_the_db_layer()
    {
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(1L, await CountEntriesAsync(conn, tx));
    }

    [Fact]
    public async Task Player_member_cannot_read_campaign_journal_at_the_db_layer()
    {
        // ผู้เล่นเป็นสมาชิกแคมเปญนี้จริง (app_is_member() = true) แต่ไม่ใช่ DM — ต้องอ่านไม่เห็นเลย
        // แม้แถวจะมีอยู่จริง (DM อ่านเจอ 1 แถวในเทสต์ด้านบน)
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(0L, await CountEntriesAsync(conn, tx));
    }

    [Fact]
    public async Task Dm_of_another_campaign_cannot_read_this_campaigns_journal()
    {
        // ตั้ง app.campaign_id เป็นแคมเปญเป้าหมาย (ไม่ใช่ของตัวเอง) — จำลองคนที่แก้ค่าที่ส่งมาเอง
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _campaignId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(0L, await CountEntriesAsync(conn, tx));
    }

    // ── INSERT: DM เท่านั้น ───────────────────────────────────────────────────

    [Fact]
    public async Task Player_member_cannot_insert_journal_entry_at_the_db_layer()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn,
                """
                INSERT INTO campaign_journal_entries (id, campaign_id, title, content, authored_by, created_at)
                VALUES (@id, @campaignId, 'ผู้เล่นแอบเขียน', 'เนื้อหา', 'Dm', now())
                """,
                tx,
                ("id", Guid.NewGuid()), ("campaignId", _campaignId)));

        Assert.Equal("42501", ex.SqlState); // insufficient_privilege — โดน RLS policy กัน
    }

    [Fact]
    public async Task Dm_can_insert_journal_entry_authored_by_ai_at_the_db_layer()
    {
        // `authored_by = 'Ai'` ต้อง insert ได้จริง (เป็นค่าที่ระบบเขียนเองตอน AI สรุปให้) —
        // ถ้า check constraint ของ enum ผิด เทสต์นี้จะพังด้วย SqlState 23514 ไม่ใช่ 42501
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await ExecAsync(conn,
            """
            INSERT INTO campaign_journal_entries (id, campaign_id, title, content, authored_by, created_at)
            VALUES (@id, @campaignId, 'สรุปโดย AI', 'ปาร์ตี้เดินทางถึงเมืองท่า', 'Ai', now())
            """,
            tx,
            ("id", Guid.NewGuid()), ("campaignId", _campaignId));

        Assert.Equal(2L, await CountEntriesAsync(conn, tx));
    }

    [Fact]
    public async Task Invalid_authored_by_value_is_rejected_by_check_constraint()
    {
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn,
                """
                INSERT INTO campaign_journal_entries (id, campaign_id, title, content, authored_by, created_at)
                VALUES (@id, @campaignId, 'ค่าที่ไม่มีใน enum', 'เนื้อหา', 'Player', now())
                """,
                tx,
                ("id", Guid.NewGuid()), ("campaignId", _campaignId)));

        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<long> CountEntriesAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM campaign_journal_entries WHERE campaign_id = @campaignId", conn, tx);
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>เปิด connection ด้วย role dndez_app แล้วตั้ง app.user_id / app.campaign_id แบบ
    /// transaction-local เหมือนที่ middleware ของ API ทำทุก request</summary>
    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAsAsync(Guid userId, Guid? campaignId = null)
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand("SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", userId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var setCampaign = new NpgsqlCommand("SELECT set_config('app.campaign_id', @campaignId, true)", conn, tx))
        {
            setCampaign.Parameters.AddWithValue("campaignId", (campaignId ?? _campaignId).ToString());
            await setCampaign.ExecuteScalarAsync();
        }

        return (conn, tx);
    }

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name) =>
        await ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", id), ("ownerId", ownerUserId), ("name", name));

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role) =>
        await ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @c, @u, @role, now())",
            ("id", Guid.NewGuid()), ("c", campaignId), ("u", userId), ("role", role));

    private static Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) =>
        ExecAsync(conn, sql, null, parameters);

    private static async Task ExecAsync(
        NpgsqlConnection conn, string sql, NpgsqlTransaction? tx, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)",
            ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId, _otherDmUserId }));
    }
}

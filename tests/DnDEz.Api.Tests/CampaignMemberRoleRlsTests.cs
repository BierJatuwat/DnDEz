using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 3 (finding "H2" จาก security review รอบก่อน — ดู `.claude/logs/routing.md`) —
/// `campaign_members` เป็น **ตารางที่นิยามว่าใครเป็น DM** แต่กลับมี policy เดียวคือ `campaign_isolation`
/// (`FOR ALL`, `app_is_member()`) ที่ตกทอดมาจาก loop "ทุกตารางที่มี campaign_id" ใน migration
/// `RowLevelSecurity` → **สมาชิกธรรมดาคนไหนก็ `UPDATE campaign_members SET role = 'Dm'` แถวตัวเองได้**
/// แล้วกลายเป็น DM ในสายตา DB ทันที
///
/// ทำไมมันร้ายแรงกว่าที่เห็น: `app_is_dm()` อ่านจากตารางนี้ตรง ๆ ดังนั้นการเลื่อนขั้นตัวเองที่นี่
/// **ปลดล็อกทุก policy ที่พึ่ง `app_is_dm()` พร้อมกันทั้งระบบ** (`autonomy_settings.dm_write`,
/// `source_documents`/`raw_chunks` `dm_only`) — เป็น privilege escalation ที่ pivot ต่อได้ ไม่ใช่
/// การแก้ข้อมูลจุดเดียว
///
/// ยืนยันจริงก่อนแก้ (2026-09-02 รันกับ Postgres ใน `dndez-postgres-1` ผ่าน role `dndez_app`):
/// `app_is_dm()` = false → `UPDATE campaign_members SET role='Dm'` คืน **UPDATE 1** → `app_is_dm()`
/// = true → INSERT ลง `autonomy_settings` ที่เป็น DM-only **สำเร็จ** (rollback ทิ้งหลังทดลอง)
///
/// ⚠️ **ยังไม่ reachable ผ่าน HTTP ตอนนี้** — grep ทั้ง `src/` แล้วไม่มีโค้ด C# เขียนตาราง
/// `campaign_members` เลยสักที่ (ที่เดียวที่แตะคือ **อ่าน** ที่ `CampaignMembershipEndpoints.cs:136`)
/// เขียนได้เฉพาะผ่าน `app_create_campaign()` / `app_join_campaign()` ซึ่งเป็น SECURITY DEFINER และ
/// hardcode role ทั้งคู่ · ปิดตอนนี้เพื่อไม่ให้ฟีเจอร์ในอนาคต (เช่น endpoint "เตะสมาชิก" ที่ต้อง
/// DELETE/UPDATE ตารางนี้) กลายเป็นตัวเปิดช่องโหว่โดยไม่มีใครทันสังเกต
///
/// contract ที่ migration (green) ต้องทำให้ผ่าน — **ต่างจาก precedent ของ `autonomy_settings` โดยตั้งใจ**:
/// `autonomy_settings` ได้ `member_read` + `dm_write` เพราะ DM เขียนผ่าน HTTP จริง (PUT autonomy-settings)
/// แต่ `campaign_members` **ไม่มีใครเขียนผ่าน HTTP เลย** จึงให้แค่ `member_read` (SELECT) และ
/// **ไม่มี write policy เลยสักตัว** — สิทธิ์ที่ให้ = สิทธิ์ที่แอปใช้จริง ไม่มากกว่านั้น
/// (การลอก `dm_write` มาด้วยจะเปิดให้ DM `DELETE` แถว role='Dm' ของตัวเองได้ ซึ่งทำลาย invariant
/// "เจ้าของแคมเปญเป็นสมาชิก role Dm เสมอ" ที่เป็นตัวเชื่อมนิยาม DM สองชั้นเข้าหากัน)
///
/// เทสต์นี้ยิงตรงที่ role `dndez_app` ผ่าน raw SQL โดยตั้งใจ (ไม่ผ่าน HTTP) — พิสูจน์ชั้น DB ล้วน ๆ
/// เหมือน <c>AutonomySettingRlsTests.cs</c>
/// </summary>
public class CampaignMemberRoleRlsTests : IAsyncLifetime
{
    // owner role — seed/cleanup เท่านั้น (superuser + BYPASSRLS จึงข้าม RLS ได้ตามธรรมชาติ)
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    // role runtime จริงของ API — ตัวเดียวกับที่ Program.cs ใช้ต่อ (RLS มีผลจริงกับ role นี้)
    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _dmUserId = Guid.NewGuid();
    private readonly Guid _playerUserId = Guid.NewGuid();
    private readonly Guid _outsiderUserId = Guid.NewGuid();

    // แคมเปญที่สอง — "เป็น DM จริง แต่คนละโต๊ะ" (code path ต่างจาก outsider ที่ไม่มีแถวเลย)
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherDmUserId = Guid.NewGuid();

    // ใช้พิสูจน์ว่าเส้นทาง join ที่ถูกกฎหมายยังทำงานได้หลังปิด write policy
    private readonly string _joinCode = $"JOIN{Guid.NewGuid():N}"[..12].ToUpperInvariant();
    private readonly Guid _joinerUserId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (id, label) in new[]
                 {
                     (_dmUserId, "dm"), (_playerUserId, "player"), (_outsiderUserId, "outsider"),
                     (_otherDmUserId, "other-dm"), (_joinerUserId, "joiner"),
                 })
        {
            await using var insertUser = new NpgsqlCommand(
                """
                INSERT INTO users (id, email, display_name, password_hash, created_at)
                VALUES (@id, @email, @label, 'not-a-real-hash', now())
                """, conn);
            insertUser.Parameters.AddWithValue("id", id);
            insertUser.Parameters.AddWithValue("email", $"member-rls-{label}-{id}@example.test");
            insertUser.Parameters.AddWithValue("label", label);
            await insertUser.ExecuteNonQueryAsync();
        }

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, "Member RLS fixture", _joinCode);
        await InsertCampaignAsync(conn, _otherCampaignId, _otherDmUserId, "Member RLS fixture (other)", null);

        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _otherDmUserId, "Dm");
    }

    private static async Task InsertCampaignAsync(
        NpgsqlConnection conn, Guid id, Guid ownerUserId, string name, string? joinCode)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at, join_code)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now(), @joinCode)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ownerId", ownerUserId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("joinCode", (object?)joinCode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertMembershipAsync(
        NpgsqlConnection conn, Guid campaignId, Guid userId, string role)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, @role, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // campaign_members ตามไปเองผ่าน ON DELETE CASCADE ของ campaign_id
        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _otherCampaignId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        await using var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn);
        deleteUsers.Parameters.AddWithValue(
            "ids", new[] { _dmUserId, _playerUserId, _outsiderUserId, _otherDmUserId, _joinerUserId });
        await deleteUsers.ExecuteNonQueryAsync();
    }

    /// <summary>อ่าน role จริงด้วย owner role — ยืนยันว่า "โดนบล็อก" คือไม่มีผลข้างเคียงจริง
    /// ไม่ใช่แค่ไม่ throw</summary>
    private async Task<string?> ReadRoleAsOwnerAsync(Guid userId, Guid? campaignId = null)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT role FROM campaign_members WHERE campaign_id = @campaignId AND user_id = @userId", conn);
        cmd.Parameters.AddWithValue("campaignId", campaignId ?? _campaignId);
        cmd.Parameters.AddWithValue("userId", userId);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAsAsync(
        Guid userId, Guid? campaignId = null)
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", userId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var setCampaign = new NpgsqlCommand(
            "SELECT set_config('app.campaign_id', @campaignId, true)", conn, tx))
        {
            setCampaign.Parameters.AddWithValue("campaignId", (campaignId ?? _campaignId).ToString());
            await setCampaign.ExecuteScalarAsync();
        }

        return (conn, tx);
    }

    // ── ช่องโหว่หลัก: เลื่อนขั้นตัวเองเป็น DM ───────────────────────────────

    [Fact]
    public async Task Player_member_cannot_promote_themselves_to_dm()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var precondition = new NpgsqlCommand("SELECT app_is_dm()", conn, tx))
        {
            Assert.False((bool)(await precondition.ExecuteScalarAsync())!);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE campaign_members SET role = 'Dm'
            WHERE campaign_id = @campaignId AND user_id = @userId
            """, conn, tx))
        {
            update.Parameters.AddWithValue("campaignId", _campaignId);
            update.Parameters.AddWithValue("userId", _playerUserId);

            // ไม่มี write policy ไหนคลุม UPDATE เลย → USING ซ่อนแถวทั้งหมด → 0 rows (ไม่ throw)
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        // ยังไม่ใช่ DM ในสายตา DB — นี่คือหัวใจ: app_is_dm() ต้องไม่พลิก
        await using (var stillNotDm = new NpgsqlCommand("SELECT app_is_dm()", conn, tx))
        {
            Assert.False((bool)(await stillNotDm.ExecuteScalarAsync())!);
        }

        await tx.CommitAsync();
        Assert.Equal("Player", await ReadRoleAsOwnerAsync(_playerUserId));
    }

    /// <summary>
    /// pivot ที่ทำให้ช่องโหว่นี้ร้ายแรง — พิสูจน์ว่าปิดต้นทางแล้วปลายทางปิดตามจริง
    /// (ก่อนแก้: promote สำเร็จ → INSERT autonomy_settings ที่เป็น DM-only สำเร็จตามไปด้วย)
    /// </summary>
    [Fact]
    public async Task Self_promotion_cannot_be_used_to_unlock_dm_only_tables()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var update = new NpgsqlCommand(
            "UPDATE campaign_members SET role = 'Dm' WHERE campaign_id = @c AND user_id = @u", conn, tx))
        {
            update.Parameters.AddWithValue("c", _campaignId);
            update.Parameters.AddWithValue("u", _playerUserId);
            await update.ExecuteNonQueryAsync();
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @campaignId, 'Narration', 'Autonomous')
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("campaignId", _campaignId);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Player_member_cannot_insert_a_membership_row()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Dm', now())
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("campaignId", _campaignId);
        insert.Parameters.AddWithValue("userId", _outsiderUserId);

        // INSERT ที่ไม่มี policy คลุม → WITH CHECK ปฏิเสธเป็น error (ต่างจาก UPDATE ที่ได้ 0 rows)
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);

        await tx.RollbackAsync();
        Assert.Null(await ReadRoleAsOwnerAsync(_outsiderUserId));
    }

    [Fact]
    public async Task Player_member_cannot_delete_a_membership_row()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        // ลบ DM ทิ้งเพื่อทำให้แคมเปญไร้ DM ก็ต้องไม่ได้
        await using var delete = new NpgsqlCommand(
            "DELETE FROM campaign_members WHERE campaign_id = @c AND user_id = @u", conn, tx);
        delete.Parameters.AddWithValue("c", _campaignId);
        delete.Parameters.AddWithValue("u", _dmUserId);
        Assert.Equal(0, await delete.ExecuteNonQueryAsync());

        await tx.CommitAsync();
        Assert.Equal("Dm", await ReadRoleAsOwnerAsync(_dmUserId));
    }

    /// <summary>
    /// ตั้งใจให้ DM เขียนไม่ได้ด้วย — ไม่ใช่การลอก `dm_write` ของ `autonomy_settings` มาแล้วลืม
    /// เหตุผล: ไม่มีโค้ด C# เขียนตารางนี้เลย (เขียนผ่าน SECURITY DEFINER เท่านั้น) และถ้าเปิดให้ DM
    /// เขียนได้ DM จะลบแถว role='Dm' ของตัวเองได้ → พัง invariant ที่เชื่อมนิยาม DM สองชั้นเข้าหากัน
    /// ถ้าวันหลังมีฟีเจอร์ "เตะสมาชิก" ให้เพิ่ม policy ใหม่อย่างตั้งใจ (แล้วเทสต์นี้จะเตือนให้แก้)
    /// </summary>
    [Fact]
    public async Task Dm_also_cannot_write_campaign_members_directly_by_design()
    {
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var isDm = new NpgsqlCommand("SELECT app_is_dm()", conn, tx))
        {
            Assert.True((bool)(await isDm.ExecuteScalarAsync())!); // เป็น DM จริง
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE campaign_members SET role = 'Player' WHERE campaign_id = @c AND user_id = @u", conn, tx))
        {
            update.Parameters.AddWithValue("c", _campaignId);
            update.Parameters.AddWithValue("u", _playerUserId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        await tx.CommitAsync();
        Assert.Equal("Player", await ReadRoleAsOwnerAsync(_playerUserId));
    }

    [Fact]
    public async Task Dm_of_another_campaign_cannot_touch_this_campaigns_members()
    {
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _campaignId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var select = new NpgsqlCommand(
            "SELECT count(*) FROM campaign_members WHERE campaign_id = @c", conn, tx))
        {
            select.Parameters.AddWithValue("c", _campaignId);
            Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE campaign_members SET role = 'Player' WHERE campaign_id = @c AND user_id = @u", conn, tx))
        {
            update.Parameters.AddWithValue("c", _campaignId);
            update.Parameters.AddWithValue("u", _dmUserId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        await tx.CommitAsync();
        Assert.Equal("Dm", await ReadRoleAsOwnerAsync(_dmUserId));
    }

    // ── อ่านต้องไม่ regress (GET /campaigns/{id}/members พึ่งอยู่) ──────────

    [Fact]
    public async Task Member_can_still_read_the_member_list()
    {
        // หัวใจของการแยก member_read ออกมา — ถ้าใครเผลอปิดทั้ง FOR ALL เทสต์นี้จะพัง
        // (GET /campaigns/{id}/members ที่ CampaignMembershipEndpoints.cs:136 พึ่ง SELECT นี้ตรง ๆ)
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM campaign_members WHERE campaign_id = @c", conn, tx);
        select.Parameters.AddWithValue("c", _campaignId);
        Assert.Equal(2, (long)(await select.ExecuteScalarAsync())!); // dm + player

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Non_member_sees_no_members_and_cannot_write()
    {
        var (conn, tx) = await OpenAsAsync(_outsiderUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var select = new NpgsqlCommand(
            "SELECT count(*) FROM campaign_members WHERE campaign_id = @c", conn, tx))
        {
            select.Parameters.AddWithValue("c", _campaignId);
            Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Dm', now())
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("campaignId", _campaignId);
            insert.Parameters.AddWithValue("userId", _outsiderUserId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await tx.RollbackAsync();
        Assert.Null(await ReadRoleAsOwnerAsync(_outsiderUserId));
    }

    [Fact]
    public async Task Query_without_app_user_id_set_sees_nothing_fail_closed()
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var select = new NpgsqlCommand("SELECT count(*) FROM campaign_members", conn, tx);
        Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);

        await tx.RollbackAsync();
    }

    // ── เส้นทางที่ถูกกฎหมายต้องไม่พัง ──────────────────────────────────────

    /// <summary>
    /// ข้อกังวลหลักของการปิด write policy: `app_join_campaign()` ยัง INSERT ได้ไหม
    ///
    /// ยืนยันแล้วว่าได้ เพราะฟังก์ชันเป็น SECURITY DEFINER ที่ **owner คือ role `dndez`** ซึ่งเป็น
    /// superuser + BYPASSRLS (ตรวจจาก `pg_roles` เมื่อ 2026-09-02) — RLS จึงถูกข้ามทั้งหมดตอนรัน
    /// ในฟังก์ชัน ไม่ว่าตารางจะตั้ง FORCE ROW LEVEL SECURITY ไว้ (ซึ่ง `campaign_members` ตั้งไว้จริง)
    /// หรือมี policy อะไรก็ตาม
    ///
    /// ⚠️ หมายเหตุที่ต้องรู้: ที่รอดไม่ใช่เพราะ "SECURITY DEFINER ข้าม RLS" (มันไม่ข้ามโดยตัวมันเอง)
    /// แต่เพราะ **owner ของฟังก์ชันเป็น superuser** ถ้าวันหลังย้าย owner ไป role ธรรมดา
    /// FORCE RLS จะมีผลทันทีและ INSERT ในฟังก์ชันนี้จะพัง — เทสต์นี้คือตัวที่จะจับให้
    /// </summary>
    [Fact]
    public async Task App_join_campaign_still_inserts_membership_after_policy_change()
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", _joinerUserId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        Guid? joinedCampaignId;
        await using (var join = new NpgsqlCommand(
            "SELECT app_join_campaign(@code, @membershipId)", conn, tx))
        {
            join.Parameters.AddWithValue("code", _joinCode);
            join.Parameters.AddWithValue("membershipId", Guid.NewGuid());
            joinedCampaignId = (Guid?)await join.ExecuteScalarAsync();
        }

        Assert.Equal(_campaignId, joinedCampaignId);

        await tx.CommitAsync();

        // role ที่ได้ต้องเป็น 'Player' เสมอ — ฟังก์ชัน hardcode ไว้ ไม่มีทางขอเป็น 'Dm'
        Assert.Equal("Player", await ReadRoleAsOwnerAsync(_joinerUserId));
    }
}

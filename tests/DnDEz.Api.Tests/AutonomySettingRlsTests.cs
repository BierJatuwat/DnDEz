using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 2a (follow-up จาก security review) — `autonomy_settings` เป็น **ตารางมอบอำนาจของ DM**
/// (AI ได้รับอนุญาตให้ทำอะไรเองได้บ้างโดยไม่ต้องขออนุมัติ) ไม่ใช่ log ที่สมาชิกทุกคนเขียนร่วมกันแบบ
/// `dice_rolls`/`messages` แต่ migration `RowLevelSecurity` จับมันเข้า loop "ทุกตารางที่มี campaign_id"
/// จนได้ policy รวม `campaign_isolation` (`app_is_member()`, FOR ALL) ไป — แปลว่า **สมาชิกคนไหนก็หมุน
/// dial ได้ในสายตา DB** ด่านเดียวที่กันอยู่คือเช็ค `campaign.OwnerUserId` ใน C# ชั้นเดียว
///
/// migration เดียวกันนั้นแยกแยะเรื่องนี้ไว้ถูกแล้วกับ `source_documents`/`raw_chunks` (ได้ policy
/// `dm_only` ที่ใช้ `app_is_dm()`) — `autonomy_settings` แค่ตกหล่นไป เทสต์นี้ล็อกการแก้ให้ตรงตาม
/// precedent เดิม
///
/// contract ที่ migration (green) ต้องทำให้ผ่าน — **สองเป็น policy แยก ไม่ใช่ `dm_only FOR ALL` ตัวเดียว**:
///   • `FOR ALL` เอา USING ไปคุม SELECT ด้วย ถ้าใช้ตัวเดียวผู้เล่นจะอ่านไม่ได้ ซึ่งผิด contract ของ
///     `GET /campaigns/{id}/autonomy-settings` ที่จงใจเปิดให้สมาชิกทุกคนอ่าน (ผู้เล่นควรรู้ว่าตอนนี้
///     AI มีอำนาจแค่ไหนในโต๊ะที่ตัวเองเล่น)
///   • RLS policy ของ Postgres เป็น PERMISSIVE โดยดีฟอลต์และ OR กันภายใน command เดียวกัน จึงแยกเป็น
///     `member_read` (FOR SELECT, app_is_member) + `dm_write` (FOR ALL, app_is_dm) ได้:
///     SELECT ของผู้เล่นผ่านทาง member_read, ส่วน INSERT/UPDATE/DELETE ไม่มี policy ไหนนอกจาก
///     dm_write เป็นตัวเลือกเลย (member_read เป็น FOR SELECT อย่างเดียว) → ผู้เล่นโดนบล็อก
///
/// เทสต์นี้ยิงตรงที่ role `dndez_app` ผ่าน raw SQL โดยตั้งใจ (ไม่ผ่าน HTTP) — จุดประสงค์คือพิสูจน์ชั้น DB
/// **ล้วน ๆ** ว่ากันได้เองแม้เช็คใน C# หายไป (defense in depth) เทสต์ระดับ HTTP ที่ครอบ contract ของ
/// endpoint อยู่ใน <c>AutonomySettingEndpointsTests.cs</c> แล้ว
/// </summary>
public class AutonomySettingRlsTests : IAsyncLifetime
{
    // owner role — seed/cleanup เท่านั้น (bypass RLS ตามธรรมชาติของ owner)
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    // role runtime จริงของ API — ตัวเดียวกับที่ Program.cs ใช้ต่อ (RLS มีผลจริงกับ role นี้)
    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _dmUserId = Guid.NewGuid();
    private readonly Guid _playerUserId = Guid.NewGuid();
    private readonly Guid _outsiderUserId = Guid.NewGuid();

    // แคมเปญที่สอง — ใช้พิสูจน์ว่า "เป็น DM จริง แต่ของอีกโต๊ะหนึ่ง" ก็ยังแตะโต๊ะนี้ไม่ได้
    // (ต่างจาก _outsiderUserId ที่ไม่มีแถวใน campaign_members เลยสักแคมเปญ — คนละ code path)
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherDmUserId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (id, label) in new[]
                 {
                     (_dmUserId, "dm"), (_playerUserId, "player"), (_outsiderUserId, "outsider"),
                     (_otherDmUserId, "other-dm"),
                 })
        {
            await using var insertUser = new NpgsqlCommand(
                """
                INSERT INTO users (id, email, display_name, password_hash, created_at)
                VALUES (@id, @email, @label, 'not-a-real-hash', now())
                """, conn);
            insertUser.Parameters.AddWithValue("id", id);
            insertUser.Parameters.AddWithValue("email", $"autonomy-rls-{label}-{id}@example.test");
            insertUser.Parameters.AddWithValue("label", label);
            await insertUser.ExecuteNonQueryAsync();
        }

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, "Autonomy RLS fixture");
        await InsertCampaignAsync(conn, _otherCampaignId, _otherDmUserId, "Autonomy RLS fixture (other table)");

        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _otherDmUserId, "Dm");
    }

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ownerId", ownerUserId);
        cmd.Parameters.AddWithValue("name", name);
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

        // autonomy_settings + campaign_members ตามไปเองผ่าน ON DELETE CASCADE ของ campaign_id
        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _otherCampaignId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        await using var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn);
        deleteUsers.Parameters.AddWithValue(
            "ids", new[] { _dmUserId, _playerUserId, _outsiderUserId, _otherDmUserId });
        await deleteUsers.ExecuteNonQueryAsync();
    }

    /// <summary>seed แถวด้วย owner role (bypass RLS) — ใช้ตั้งต้นเคส UPDATE/DELETE/SELECT</summary>
    private async Task SeedSettingAsync(string subsystem, string level)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @campaignId, @subsystem, @level)
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        cmd.Parameters.AddWithValue("subsystem", subsystem);
        cmd.Parameters.AddWithValue("level", level);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>อ่านค่าจริงในตารางด้วย owner role — ยืนยันว่า "โดนบล็อก" คือไม่มีผลข้างเคียงจริง ๆ
    /// ไม่ใช่แค่ไม่ throw</summary>
    private async Task<string?> ReadLevelAsOwnerAsync(string subsystem)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT level FROM autonomy_settings WHERE campaign_id = @campaignId AND subsystem = @subsystem",
            conn);
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        cmd.Parameters.AddWithValue("subsystem", subsystem);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    /// <summary>เปิด connection ด้วย role dndez_app แล้วตั้ง app.user_id / app.campaign_id แบบ
    /// transaction-local เหมือนที่ middleware ของ API ทำทุก request</summary>
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

    // ── write: DM เท่านั้น ────────────────────────────────────────────────

    [Fact]
    public async Task Player_member_cannot_insert_autonomy_setting_at_the_db_layer()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @campaignId, 'Narration', 'Autonomous')
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("campaignId", _campaignId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege = WITH CHECK ของ RLS ปฏิเสธ

        await tx.RollbackAsync();
        Assert.Null(await ReadLevelAsOwnerAsync("Narration"));
    }

    [Fact]
    public async Task Player_member_cannot_update_existing_autonomy_setting_at_the_db_layer()
    {
        await SeedSettingAsync("RulesCombat", "Manual");

        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var update = new NpgsqlCommand(
            "UPDATE autonomy_settings SET level = 'Autonomous' WHERE campaign_id = @campaignId AND subsystem = 'RulesCombat'",
            conn, tx);
        update.Parameters.AddWithValue("campaignId", _campaignId);

        // USING ของ dm_write ซ่อนแถวจากคำสั่ง UPDATE → 0 rows affected (ไม่ throw, แต่ไม่มีผล)
        Assert.Equal(0, await update.ExecuteNonQueryAsync());

        await tx.CommitAsync();
        Assert.Equal("Manual", await ReadLevelAsOwnerAsync("RulesCombat"));
    }

    [Fact]
    public async Task Player_member_cannot_delete_autonomy_setting_at_the_db_layer()
    {
        await SeedSettingAsync("MapScene", "Manual");

        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var delete = new NpgsqlCommand(
            "DELETE FROM autonomy_settings WHERE campaign_id = @campaignId AND subsystem = 'MapScene'",
            conn, tx);
        delete.Parameters.AddWithValue("campaignId", _campaignId);

        Assert.Equal(0, await delete.ExecuteNonQueryAsync());

        await tx.CommitAsync();
        Assert.Equal("Manual", await ReadLevelAsOwnerAsync("MapScene"));
    }

    [Fact]
    public async Task Dm_can_insert_and_update_autonomy_setting_at_the_db_layer()
    {
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @campaignId, 'NpcDialogue', 'Manual')
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("campaignId", _campaignId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE autonomy_settings SET level = 'Autonomous' WHERE campaign_id = @campaignId AND subsystem = 'NpcDialogue'",
            conn, tx))
        {
            update.Parameters.AddWithValue("campaignId", _campaignId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await tx.CommitAsync();
        Assert.Equal("Autonomous", await ReadLevelAsOwnerAsync("NpcDialogue"));
    }

    [Fact]
    public async Task Dm_can_delete_autonomy_setting_at_the_db_layer()
    {
        await SeedSettingAsync("Narration", "Suggest");

        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var delete = new NpgsqlCommand(
            "DELETE FROM autonomy_settings WHERE campaign_id = @campaignId AND subsystem = 'Narration'",
            conn, tx);
        delete.Parameters.AddWithValue("campaignId", _campaignId);
        Assert.Equal(1, await delete.ExecuteNonQueryAsync());

        await tx.CommitAsync();
        Assert.Null(await ReadLevelAsOwnerAsync("Narration"));
    }

    // ── read: สมาชิกทุกคน (ห้าม regress เป็น DM-only) ─────────────────────

    [Fact]
    public async Task Player_member_can_still_read_autonomy_settings()
    {
        // หัวใจของการแยกเป็นสองนโยบาย — ถ้าใครรวบเป็น `dm_only FOR ALL` ตัวเดียว เทสต์นี้จะพัง
        await SeedSettingAsync("Narration", "Autonomous");
        await SeedSettingAsync("MapScene", "Manual");

        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        var levelsBySubsystem = new Dictionary<string, string>();
        await using (var select = new NpgsqlCommand(
            "SELECT subsystem, level FROM autonomy_settings WHERE campaign_id = @campaignId", conn, tx))
        {
            select.Parameters.AddWithValue("campaignId", _campaignId);
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                levelsBySubsystem[reader.GetString(0)] = reader.GetString(1);
            }
        }

        Assert.Equal("Autonomous", levelsBySubsystem["Narration"]);
        Assert.Equal("Manual", levelsBySubsystem["MapScene"]);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Dm_can_read_autonomy_settings()
    {
        await SeedSettingAsync("Narration", "Autonomous");

        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM autonomy_settings WHERE campaign_id = @campaignId", conn, tx);
        select.Parameters.AddWithValue("campaignId", _campaignId);
        Assert.Equal(1, (long)(await select.ExecuteScalarAsync())!);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Non_member_sees_nothing_and_cannot_write()
    {
        await SeedSettingAsync("Narration", "Autonomous");

        var (conn, tx) = await OpenAsAsync(_outsiderUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var select = new NpgsqlCommand(
            "SELECT count(*) FROM autonomy_settings WHERE campaign_id = @campaignId", conn, tx))
        {
            select.Parameters.AddWithValue("campaignId", _campaignId);
            Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @campaignId, 'NpcDialogue', 'Autonomous')
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("campaignId", _campaignId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await tx.RollbackAsync();
        Assert.Null(await ReadLevelAsOwnerAsync("NpcDialogue"));
    }

    // ── cross-campaign: "เป็น DM จริง แต่คนละโต๊ะ" ────────────────────────

    [Fact]
    public async Task Dm_of_another_campaign_cannot_read_or_write_this_campaigns_settings()
    {
        // code-reviewer finding (รอบ 1): `Non_member_sees_nothing_and_cannot_write` ครอบแค่คนที่ไม่มี
        // แถวใน campaign_members เลยสักแคมเปญ ซึ่งไม่ได้เดินผ่าน path เดียวกับ "มีแถวจริง แต่ผิดแคมเปญ"
        // เคสหลังนี่แหละที่จะพังถ้าวันหลังมีคนถอด conjunct `m.campaign_id = app_current_campaign()`
        // ออกจาก app_is_dm()/app_is_member() — จะกลายเป็น "เป็น DM ที่ไหนสักแห่ง = เป็น DM ทุกที่"
        await SeedSettingAsync("Narration", "Autonomous");

        // ตั้ง app.campaign_id เป็นแคมเปญ *เป้าหมาย* (ไม่ใช่ของตัวเอง) — จำลองคนที่แก้ค่าที่ส่งมาเอง
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _campaignId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var select = new NpgsqlCommand(
            "SELECT count(*) FROM autonomy_settings WHERE campaign_id = @campaignId", conn, tx))
        {
            select.Parameters.AddWithValue("campaignId", _campaignId);
            Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE autonomy_settings SET level = 'Manual' WHERE campaign_id = @campaignId AND subsystem = 'Narration'",
            conn, tx))
        {
            update.Parameters.AddWithValue("campaignId", _campaignId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @campaignId, 'MapScene', 'Autonomous')
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("campaignId", _campaignId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await tx.RollbackAsync();

        // ค่าเดิมของโต๊ะเป้าหมายต้องไม่ถูกแตะเลย
        Assert.Equal("Autonomous", await ReadLevelAsOwnerAsync("Narration"));
        Assert.Null(await ReadLevelAsOwnerAsync("MapScene"));
    }

    [Fact]
    public async Task Dm_cannot_write_a_row_belonging_to_a_campaign_other_than_the_session_campaign()
    {
        // แยก conjunct `campaign_id = app_current_campaign()` ออกมาทดสอบเดี่ยว ๆ จาก app_is_dm():
        // ที่นี่ app_is_dm() เป็น true (เป็น DM ของ _campaignId จริง และ app.campaign_id ก็ตั้งเป็น
        // _campaignId) — สิ่งเดียวที่กันไว้คือ WITH CHECK ที่บังคับว่าแถวที่เขียนต้องเป็นของแคมเปญ
        // ใน session เท่านั้น ห้ามยัด campaign_id ของโต๊ะอื่นลงไป
        var (conn, tx) = await OpenAsAsync(_dmUserId, _campaignId);
        await using var _ = conn;
        await using var __ = tx;

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @otherCampaignId, 'Narration', 'Autonomous')
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("otherCampaignId", _otherCampaignId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Query_without_app_user_id_set_sees_nothing_fail_closed()
    {
        await SeedSettingAsync("Narration", "Autonomous");

        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // ไม่ตั้ง app.user_id / app.campaign_id เลยโดยตั้งใจ
        await using var select = new NpgsqlCommand("SELECT count(*) FROM autonomy_settings", conn, tx);
        Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);

        await tx.RollbackAsync();
    }
}

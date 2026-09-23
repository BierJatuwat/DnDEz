using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Part C ของแผน "DM AI Assistant" — `consult_messages` เก็บ**บทสนทนากับผู้ช่วย DM** (2026-09-23:
/// ปัจจุบันคือผู้ช่วย DM ช่องเดียว `/assistant` — ก่อนหน้านี้เป็น `/advisor`) ซึ่งเห็น
/// `StoryBeat.DmNotes`/`Secrets` ได้ (ต่างจาก `/actions` ที่ broadcast และห้ามเห็นความลับเด็ดขาด)
/// — คำตอบของ AI จึงอ้างถึงความลับของแคมเปญได้ตรง ๆ **ผู้เล่นต้องอ่านไม่ได้แม้แต่แถวเดียวที่ชั้น DB**
/// ไม่ใช่พึ่งเช็คใน C# ชั้นเดียว
///
/// ตารางนี้เคยได้ policy รวม `campaign_isolation` (`app_is_member()`, FOR ALL) มาจาก loop "ทุกตารางที่มี
/// campaign_id" ของ migration `RowLevelSecurity` — เคสเดียวกับที่ `autonomy_settings` เคยเจอแล้วต้องมาแก้
/// (`AddAutonomySettingDmOnlyRls`) เทสต์นี้ล็อกผลของ `AddConsultMessageDmOnlyRls`
///
/// ⚠️ **ต่างจาก `AutonomySettingRlsTests` ตรงที่นี่ใช้ `dm_only FOR ALL` ตัวเดียวถูกต้องแล้ว** — ที่นั่น
/// ต้องแยก `member_read` + `dm_write` เพราะผู้เล่น *ควร* อ่าน dial ได้ แต่แชทส่วนตัวของ DM ผู้เล่นไม่ควร
/// อ่านได้เลย การที่ `FOR ALL` เอา USING ไปคุม SELECT ด้วยจึงเป็นผลที่ต้องการพอดี — เทสต์นี้พิสูจน์ด้วย
/// ของจริง ไม่ใช่เชื่อเหตุผลบนกระดาษ (บทเรียน Sprint F งานที่ 2a: "false fix ที่ดูถูกจนกว่าจะวัดจริง")
///
/// ยิงตรงที่ role `dndez_app` ผ่าน raw SQL โดยตั้งใจ (ไม่ผ่าน HTTP) — พิสูจน์ชั้น DB ล้วน ๆ ว่ากันได้เอง
/// แม้เช็คใน C# หายไป · contract ระดับ endpoint อยู่ใน <c>AssistantEndpointsTests.cs</c>
/// </summary>
public class ConsultMessageRlsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _dmUserId = Guid.NewGuid();
    private readonly Guid _playerUserId = Guid.NewGuid();
    private readonly Guid _existingMessageId = Guid.NewGuid();

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
                ("id", id), ("email", $"consult-rls-{label}-{id}@example.test"), ("label", label));
        }

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, "Consult RLS fixture");
        await InsertCampaignAsync(conn, _otherCampaignId, _otherDmUserId, "Consult RLS fixture (other table)");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _otherDmUserId, "Dm");

        // แถวที่มีอยู่จริง — ถ้าผู้เล่น SELECT แล้วได้ 0 แปลว่า RLS กันจริง ไม่ใช่ตารางว่างเปล่า
        await ExecAsync(conn,
            """
            INSERT INTO consult_messages (id, campaign_id, dm_user_id, role, content, created_at)
            VALUES (@id, @campaignId, @dmUserId, 'Dm', 'แผนลับของวายร้ายในบทนี้', now())
            """,
            ("id", _existingMessageId), ("campaignId", _campaignId), ("dmUserId", _dmUserId));
    }

    // ── SELECT: DM เท่านั้น (จุดที่ต่างจาก autonomy_settings) ─────────────────

    [Fact]
    public async Task Dm_can_read_own_campaign_consult_messages_at_the_db_layer()
    {
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(1L, await CountMessagesAsync(conn, tx));
    }

    [Fact]
    public async Task Player_member_cannot_read_consult_messages_at_the_db_layer()
    {
        // ผู้เล่นเป็นสมาชิกแคมเปญนี้จริง (app_is_member() = true) แต่ไม่ใช่ DM — ต้องอ่านไม่เห็นเลย
        // แม้แถวจะมีอยู่จริง (DM อ่านเจอ 1 แถวในเทสต์ด้านบน)
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(0L, await CountMessagesAsync(conn, tx));
    }

    [Fact]
    public async Task Dm_of_another_campaign_cannot_read_this_campaigns_consult_messages()
    {
        // ตั้ง app.campaign_id เป็นแคมเปญเป้าหมาย (ไม่ใช่ของตัวเอง) — จำลองคนที่แก้ค่าที่ส่งมาเอง
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _campaignId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(0L, await CountMessagesAsync(conn, tx));
    }

    // ── INSERT: DM เท่านั้น ──────────────────────────────────────────────────

    [Fact]
    public async Task Player_member_cannot_insert_consult_message_at_the_db_layer()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn,
                """
                INSERT INTO consult_messages (id, campaign_id, dm_user_id, role, content, created_at)
                VALUES (@id, @campaignId, @dmUserId, 'Dm', 'ผู้เล่นแอบเขียน', now())
                """,
                tx,
                ("id", Guid.NewGuid()), ("campaignId", _campaignId), ("dmUserId", _playerUserId)));

        Assert.Equal("42501", ex.SqlState); // insufficient_privilege — โดน RLS policy กัน
    }

    [Fact]
    public async Task Dm_can_insert_consult_message_at_the_db_layer()
    {
        var (conn, tx) = await OpenAsAsync(_dmUserId);
        await using var _ = conn;
        await using var __ = tx;

        await ExecAsync(conn,
            """
            INSERT INTO consult_messages (id, campaign_id, dm_user_id, role, content, created_at)
            VALUES (@id, @campaignId, @dmUserId, 'Ai', 'คำแนะนำจาก AI', now())
            """,
            tx,
            ("id", Guid.NewGuid()), ("campaignId", _campaignId), ("dmUserId", _dmUserId));

        Assert.Equal(2L, await CountMessagesAsync(conn, tx));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<long> CountMessagesAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM consult_messages WHERE campaign_id = @campaignId", conn, tx);
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

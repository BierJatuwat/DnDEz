using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 4 — พิสูจน์ RLS ของสองตารางใหม่ (`encounters`, `encounter_participants`)
/// ที่ชั้น DB ล้วน ๆ ผ่าน role `dndez_app` ด้วย raw SQL **ก่อน/คู่ขนานกับชั้น C#** ตามแบบแผนที่
/// โปรเจกต์นี้ทำมาตลอด (`AutonomySettingRlsTests`, `CampaignMemberRoleRlsTests`,
/// `SecurityDefinerSearchPathTests`) — ไม่ยิงผ่าน HTTP โดยตั้งใจ
///
/// ⚠️ **กับดักที่เทสต์นี้มีไว้จับโดยเฉพาะ** (ตรวจจริงกับโค้ด 2026-09-03 ไม่ใช่ข้อสันนิษฐาน):
/// loop `FOR t IN SELECT ... WHERE column_name = 'campaign_id'` ใน migration `RowLevelSecurity`
/// (20260821094727 บรรทัด ~68-88) เป็น **`DO $$` block ที่รันครั้งเดียวตอน migrate ครั้งนั้น**
/// ไม่ใช่ event trigger — ตารางที่ถูกสร้างใน migration รุ่นหลังจึง **ไม่ได้รับ policy อัตโนมัติ**
/// แม้จะมีคอลัมน์ `campaign_id` ก็ตาม และเพราะ `ALTER DEFAULT PRIVILEGES ... GRANT SELECT, INSERT,
/// UPDATE, DELETE ON TABLES TO dndez_app` (บรรทัด 31-32) ยังมีผลกับตารางใหม่อยู่
/// ผลลัพธ์ที่จะเกิดถ้าลืมเขียน policy เองคือ **ตารางใหม่เปิดอ่าน/เขียนข้ามแคมเปญได้ทั้งหมด**
/// (มี GRANT แต่ไม่มี RLS = fail **open**) — เทสต์ <see cref="Rls_is_enabled_and_forced_on_both_tables"/>
/// กับเทสต์ cross-campaign ด้านล่างคือตาข่ายที่จับเรื่องนี้
/// </summary>
public class EncounterRlsTests : IAsyncLifetime
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
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _characterId = Guid.NewGuid();
    private readonly Guid _encounterId = Guid.NewGuid();
    private readonly Guid _participantId = Guid.NewGuid();

    // แคมเปญที่สอง — "เป็นสมาชิก/DM จริง แต่คนละโต๊ะ" (code path ต่างจาก outsider ที่ไม่มีแถวเลย)
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherDmUserId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();
    private readonly Guid _otherCharacterId = Guid.NewGuid();
    private readonly Guid _otherEncounterId = Guid.NewGuid();
    private readonly Guid _otherParticipantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (id, label) in new[]
                 {
                     (_dmUserId, "dm"), (_playerUserId, "player"),
                     (_outsiderUserId, "outsider"), (_otherDmUserId, "other-dm"),
                 })
        {
            await using var insertUser = new NpgsqlCommand(
                """
                INSERT INTO users (id, email, display_name, password_hash, created_at)
                VALUES (@id, @email, @label, 'not-a-real-hash', now())
                """, conn);
            insertUser.Parameters.AddWithValue("id", id);
            insertUser.Parameters.AddWithValue("email", $"encounter-rls-{label}-{id}@example.test");
            insertUser.Parameters.AddWithValue("label", label);
            await insertUser.ExecuteNonQueryAsync();
        }

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, "Encounter RLS fixture");
        await InsertCampaignAsync(conn, _otherCampaignId, _otherDmUserId, "Encounter RLS fixture (other)");

        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _otherDmUserId, "Dm");

        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _otherSessionId, _otherCampaignId);

        await InsertCharacterAsync(conn, _characterId, _campaignId, "Fighter");
        await InsertCharacterAsync(conn, _otherCharacterId, _otherCampaignId, "Other Fighter");

        await InsertEncounterAsync(conn, _encounterId, _campaignId, _sessionId);
        await InsertEncounterAsync(conn, _otherEncounterId, _otherCampaignId, _otherSessionId);

        await InsertParticipantAsync(conn, _participantId, _encounterId, _characterId);
        await InsertParticipantAsync(conn, _otherParticipantId, _otherEncounterId, _otherCharacterId);
    }

    private static async Task InsertCampaignAsync(
        NpgsqlConnection conn, Guid id, Guid ownerUserId, string name)
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

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @campaignId, 'Online', 'Active', NULL, now(), NULL)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, NULL, @name, '{}', 20, 20, 0, 10, 0, ARRAY[]::text[],
                 ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertEncounterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, Guid sessionId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO encounters
                (id, campaign_id, session_id, current_participant_id, started_at, ended_at)
            VALUES (@id, @campaignId, @sessionId, NULL, now(), NULL)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertParticipantAsync(
        NpgsqlConnection conn, Guid id, Guid encounterId, Guid characterId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO encounter_participants
                (id, encounter_id, character_id, initiative_roll, initiative_modifier,
                 initiative_total, turn_order)
            VALUES (@id, @encounterId, @characterId, 10, 0, 10, 0)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("encounterId", encounterId);
        cmd.Parameters.AddWithValue("characterId", characterId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // encounters/encounter_participants ตามไปเองผ่าน ON DELETE CASCADE ของ campaign_id/encounter_id
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

    private static async Task<long> CountAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Guid parameterValue)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("p", parameterValue);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── ตาข่ายจับ "ลืมเปิด RLS ให้ตารางใหม่" (ดู doc comment ด้านบน) ─────────

    [Fact]
    public async Task Rls_is_enabled_and_forced_on_both_tables()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var table in new[] { "encounters", "encounter_participants" })
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT relrowsecurity, relforcerowsecurity
                FROM pg_class
                WHERE oid = ('public.' || @table)::regclass
                """, conn);
            cmd.Parameters.AddWithValue("table", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"ไม่พบตาราง {table}");
            Assert.True(reader.GetBoolean(0), $"{table}: ENABLE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
            Assert.True(reader.GetBoolean(1), $"{table}: FORCE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
        }
    }

    [Fact]
    public async Task Both_tables_have_at_least_one_policy()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var table in new[] { "encounters", "encounter_participants" })
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = @table", conn);
            cmd.Parameters.AddWithValue("table", table);
            Assert.True((long)(await cmd.ExecuteScalarAsync())! > 0, $"{table}: ไม่มี policy เลย");
        }
    }

    // ── encounters ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Member_can_read_encounters_of_their_own_campaign()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(1, await CountAsync(
            conn, tx, "SELECT count(*) FROM encounters WHERE id = @p", _encounterId));

        await tx.RollbackAsync();
    }

    /// <summary>
    /// หัวใจของงานนี้ — DM ของโต๊ะอื่น (สมาชิกจริง มีสิทธิ์จริง แค่คนละแคมเปญ) ต้องมองไม่เห็น
    /// encounter ของโต๊ะนี้เลย แม้จะรู้ id ตรง ๆ
    /// </summary>
    [Fact]
    public async Task Dm_of_another_campaign_cannot_see_this_campaigns_encounter()
    {
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _campaignId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(0, await CountAsync(
            conn, tx, "SELECT count(*) FROM encounters WHERE id = @p", _encounterId));

        await tx.RollbackAsync();
    }

    /// <summary>
    /// เคสที่ร้ายกว่า: ตั้ง app.campaign_id เป็นแคมเปญ**ของตัวเอง** แล้วอ่านทั้งตาราง —
    /// ถ้า policy ผูกกับ campaign_id ถูกต้อง จะเห็นเฉพาะ encounter ของตัวเอง ไม่ปนของโต๊ะอื่น
    /// </summary>
    [Fact]
    public async Task Full_table_scan_returns_only_own_campaigns_encounters()
    {
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _otherCampaignId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var all = new NpgsqlCommand("SELECT count(*) FROM encounters", conn, tx))
        {
            Assert.Equal(1, (long)(await all.ExecuteScalarAsync())!);
        }

        Assert.Equal(1, await CountAsync(
            conn, tx, "SELECT count(*) FROM encounters WHERE id = @p", _otherEncounterId));

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Outsider_sees_no_encounters()
    {
        var (conn, tx) = await OpenAsAsync(_outsiderUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var all = new NpgsqlCommand("SELECT count(*) FROM encounters", conn, tx))
        {
            Assert.Equal(0, (long)(await all.ExecuteScalarAsync())!);
        }

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Outsider_cannot_insert_an_encounter_into_someone_elses_campaign()
    {
        var (conn, tx) = await OpenAsAsync(_outsiderUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO encounters (id, campaign_id, session_id, current_participant_id, started_at, ended_at)
            VALUES (@id, @campaignId, @sessionId, NULL, now(), NULL)
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("campaignId", _campaignId);
        insert.Parameters.AddWithValue("sessionId", _sessionId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Encounters_fail_closed_without_app_user_id_set()
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var select = new NpgsqlCommand("SELECT count(*) FROM encounters", conn, tx);
        Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);

        await tx.RollbackAsync();
    }

    // ── encounter_participants (ไม่มี campaign_id ตรง ๆ — ต้องพึ่ง EXISTS-chain) ──

    [Fact]
    public async Task Member_can_read_participants_of_their_own_campaigns_encounter()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(1, await CountAsync(
            conn, tx, "SELECT count(*) FROM encounter_participants WHERE id = @p", _participantId));

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Dm_of_another_campaign_cannot_see_this_campaigns_participants()
    {
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _campaignId);
        await using var _ = conn;
        await using var __ = tx;

        Assert.Equal(0, await CountAsync(
            conn, tx, "SELECT count(*) FROM encounter_participants WHERE id = @p", _participantId));

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Full_table_scan_returns_only_own_campaigns_participants()
    {
        var (conn, tx) = await OpenAsAsync(_otherDmUserId, _otherCampaignId);
        await using var _ = conn;
        await using var __ = tx;

        await using var all = new NpgsqlCommand("SELECT count(*) FROM encounter_participants", conn, tx);
        Assert.Equal(1, (long)(await all.ExecuteScalarAsync())!);

        await tx.RollbackAsync();
    }

    /// <summary>
    /// IDOR ที่ตารางลูกเปิดช่องได้ง่ายที่สุด — สมาชิกโต๊ะ A ยัด participant เข้า encounter ของโต๊ะ B
    /// (รู้ id ของ encounter อีกโต๊ะ) ต้องถูก WITH CHECK ปฏิเสธ
    /// </summary>
    [Fact]
    public async Task Member_cannot_insert_participant_into_another_campaigns_encounter()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO encounter_participants
                (id, encounter_id, character_id, initiative_roll, initiative_modifier,
                 initiative_total, turn_order)
            VALUES (@id, @encounterId, @characterId, 20, 0, 20, 1)
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("encounterId", _otherEncounterId);
        insert.Parameters.AddWithValue("characterId", _otherCharacterId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);

        await tx.RollbackAsync();
    }

    /// <summary>
    /// การแก้ turn_order ของโต๊ะอื่นต้องได้ 0 rows (USING ซ่อนแถวไว้ก่อนถึง SET ด้วยซ้ำ) —
    /// แยกเทสต์จาก INSERT เพราะ UPDATE/DELETE ที่ไม่มีแถวให้แก้ **ไม่ throw** (บทเรียนเดียวกับ
    /// `CampaignMemberRoleRlsTests`)
    /// </summary>
    [Fact]
    public async Task Member_cannot_update_another_campaigns_participant()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var update = new NpgsqlCommand(
            "UPDATE encounter_participants SET turn_order = 99 WHERE id = @p", conn, tx))
        {
            update.Parameters.AddWithValue("p", _otherParticipantId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        await tx.CommitAsync();

        await using var owner = new NpgsqlConnection(OwnerConnectionString);
        await owner.OpenAsync();
        await using var read = new NpgsqlCommand(
            "SELECT turn_order FROM encounter_participants WHERE id = @p", owner);
        read.Parameters.AddWithValue("p", _otherParticipantId);
        Assert.Equal(0, (int)(await read.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Participants_fail_closed_without_app_user_id_set()
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM encounter_participants", conn, tx);
        Assert.Equal(0, (long)(await select.ExecuteScalarAsync())!);

        await tx.RollbackAsync();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 5 — death save state machine ผ่าน HTTP จริง + Postgres จริง
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// POST /campaigns/{campaignId}/characters/{characterId}/death-saves/confirm-dying   [DM เท่านั้น]
///      200 + DeathSaveStateResponse (Successes=0, Failures=0, Status="Rolling")
///      400 = ตัวละครไม่ได้อยู่ที่ 0 HP (ยืนยัน "กำลังจะตาย" ให้คนที่ยังมี HP ไม่มีความหมาย)
///      403 = สมาชิกที่ไม่ใช่ DM (รวมถึง**เจ้าของตัวละครเอง** — decision ล็อกไว้ว่า DM คุม pacing)
///      404 = ไม่ใช่สมาชิก / ตัวละครอยู่คนละแคมเปญ · 409 = มีสถานะ Rolling/Stable/Dead อยู่แล้ว
///
/// POST /campaigns/{campaignId}/sessions/{sessionId}/characters/{characterId}/death-saves/roll
///      [เจ้าของตัวละคร **หรือ** DM ที่คุมตัวละคร ControlledByDm — RequireCharacterActorAsync]
///      200 + DeathSaveRollResponse · 400 = DiceMode ไม่ใช่ SystemRoll
///      403 = สมาชิกที่ไม่ใช่เจ้าของ/ไม่ใช่ DM ที่คุมอยู่ · 404 = ไม่ใช่สมาชิก / ตัวละคร/session คนละแคมเปญ
///      409 = ไม่มีแถว death save หรือสถานะไม่ใช่ Rolling ("ยังไม่ได้กำลังจะตาย")
///
/// GET  /campaigns/{campaignId}/characters/{characterId}/death-saves
///      PartyVisible → สมาชิกทุกคนอ่านได้ · DmOnly → **403 สำหรับสมาชิกที่ไม่ใช่ DM**
///      ไม่มีแถว = 200 + Status "NotDying" (ไม่ใช่ 404 — "ไม่ได้กำลังจะตาย" เป็นสถานะปกติ ไม่ใช่ของหาย)
///
/// ⚠️ ทำไม roll ถึงซ้อนใต้ /sessions/{sessionId} ต่างจากอีกสอง endpoint:
/// `dice_rolls.session_id` เป็น `uuid NOT NULL` + FK ไป `sessions` (ดู DnDEzDbContextModelSnapshot)
/// การทอย death save ต้องบันทึกลง dice_rolls เหมือนทุกการทอยในระบบ จึงต้องมี session จริงมาผูก —
/// รูป URL เดียวกับ CombatEndpoints (checks/attacks) เป๊ะ ๆ ส่วน confirm-dying/GET ไม่ทอยเต๋าเลย
/// จึงไม่ต้องมี session ใน path
///
/// ⚠️ การทอยเป็น CSPRNG จริงเหมือนทั้งโปรเจกต์ (ห้าม mock) — เทสต์จึงยืนยันแบบ property-based:
/// อ่านค่า d20 ดิบที่ response ส่งกลับมา แล้วยืนยันว่า outcome/ตัวนับ/HP สอดคล้องกับกติกาของ**ค่านั้น**
/// (nat20 ฟื้น 1 HP, nat1 = 2 failures, 10+ success, 2-9 failure) ไม่ได้พึ่งโชคว่าจะออกอะไร
/// </summary>
public class DeathSaveEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "death-save-endpoints-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();          // PartyVisible
    private readonly Guid _dmOnlyCampaignId = Guid.NewGuid();    // DmOnly
    private readonly Guid _dmEnteredCampaignId = Guid.NewGuid(); // DiceMode = DmEntered
    private readonly Guid _otherCampaignId = Guid.NewGuid();     // outsider เป็น DM

    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _dmOnlySessionId = Guid.NewGuid();
    private readonly Guid _dmEnteredSessionId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();

    // ตัวละครในแคมเปญหลัก
    private readonly Guid _zeroHpCharacterId = Guid.NewGuid();       // playerA, 0/20 HP
    private readonly Guid _healthyCharacterId = Guid.NewGuid();      // playerA, 20/20 HP, AC 1
    private readonly Guid _dmControlledCharacterId = Guid.NewGuid(); // owner NULL, controlled_by_dm = true, 0/20
    private readonly Guid _playerBCharacterId = Guid.NewGuid();      // playerB, 0/20
    private readonly Guid _attackerCharacterId = Guid.NewGuid();     // playerA, 20/20 — ตัวที่ลงมือโจมตี
    private readonly Guid _dyingTargetCharacterId = Guid.NewGuid();  // playerB, 0/20, AC 1 — เป้าโจมตีตอน 0 HP

    private readonly Guid _dmOnlyCharacterId = Guid.NewGuid();       // playerA, 0/20 (แคมเปญ DmOnly)
    private readonly Guid _dmEnteredCharacterId = Guid.NewGuid();    // playerA, 0/20 (แคมเปญ DmEntered)
    private readonly Guid _otherCharacterId = Guid.NewGuid();        // outsider (แคมเปญอื่น)

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerAUserId;
    private string _playerAToken = null!;
    private Guid _playerBUserId;
    private string _playerBToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerAUserId, _playerAToken) = await RegisterAsync("player-a");
        (_playerBUserId, _playerBToken) = await RegisterAsync("player-b");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"DS Campaign {_suffix}", "PartyVisible", "SystemRoll");
        await InsertCampaignAsync(conn, _dmOnlyCampaignId, _dmUserId, $"DS DmOnly {_suffix}", "DmOnly", "SystemRoll");
        await InsertCampaignAsync(conn, _dmEnteredCampaignId, _dmUserId, $"DS DmEntered {_suffix}", "PartyVisible", "DmEntered");
        await InsertCampaignAsync(conn, _otherCampaignId, _outsiderUserId, $"DS Other {_suffix}", "PartyVisible", "SystemRoll");

        foreach (var campaignId in new[] { _campaignId, _dmOnlyCampaignId, _dmEnteredCampaignId })
        {
            await InsertMembershipAsync(conn, campaignId, _dmUserId, "Dm");
            await InsertMembershipAsync(conn, campaignId, _playerAUserId, "Player");
            await InsertMembershipAsync(conn, campaignId, _playerBUserId, "Player");
        }

        await InsertMembershipAsync(conn, _otherCampaignId, _outsiderUserId, "Dm");

        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _dmOnlySessionId, _dmOnlyCampaignId);
        await InsertSessionAsync(conn, _dmEnteredSessionId, _dmEnteredCampaignId);
        await InsertSessionAsync(conn, _otherSessionId, _otherCampaignId);

        await InsertCharacterAsync(conn, _zeroHpCharacterId, _campaignId, "Zero", _playerAUserId, currentHp: 0);
        await InsertCharacterAsync(conn, _healthyCharacterId, _campaignId, "Healthy", _playerAUserId, currentHp: 20, armorClass: 1);
        await InsertCharacterAsync(conn, _dmControlledCharacterId, _campaignId, "DmPet", null, currentHp: 0, controlledByDm: true);
        await InsertCharacterAsync(conn, _playerBCharacterId, _campaignId, "PlayerB PC", _playerBUserId, currentHp: 0);
        await InsertCharacterAsync(conn, _attackerCharacterId, _campaignId, "Attacker", _playerAUserId, currentHp: 20);
        await InsertCharacterAsync(conn, _dyingTargetCharacterId, _campaignId, "Dying", _playerBUserId, currentHp: 0, armorClass: 1);
        await InsertCharacterAsync(conn, _dmOnlyCharacterId, _dmOnlyCampaignId, "DmOnly PC", _playerAUserId, currentHp: 0);
        await InsertCharacterAsync(conn, _dmEnteredCharacterId, _dmEnteredCampaignId, "DmEntered PC", _playerAUserId, currentHp: 0);
        await InsertCharacterAsync(conn, _otherCharacterId, _otherCampaignId, "Other PC", _outsiderUserId, currentHp: 0);
    }

    // ── fixture helpers ─────────────────────────────────────────────────────

    private static async Task InsertCampaignAsync(
        NpgsqlConnection conn, Guid id, Guid ownerUserId, string name, string deathSaveVisibility, string diceMode)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', @diceMode, @visibility, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ownerId", ownerUserId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("diceMode", diceMode);
        cmd.Parameters.AddWithValue("visibility", deathSaveVisibility);
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
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, Guid? ownerUserId,
        int currentHp, int armorClass = 10, bool controlledByDm = false)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}', @currentHp, 20, 0, @armorClass, 0,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @controlledByDm, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("currentHp", currentHp);
        cmd.Parameters.AddWithValue("armorClass", armorClass);
        cmd.Parameters.AddWithValue("controlledByDm", controlledByDm);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue(
                "ids", new[] { _campaignId, _dmOnlyCampaignId, _dmEnteredCampaignId, _otherCampaignId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue(
                "ids", new[] { _dmUserId, _playerAUserId, _playerBUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"deathsave-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"DeathSave {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── db helpers ──────────────────────────────────────────────────────────

    private static async Task UpsertDeathSaveAsync(
        Guid characterId, int successes, int failures, string status)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO death_save_states (character_id, successes, failures, status, updated_at)
            VALUES (@id, @s, @f, @status, now())
            ON CONFLICT (character_id) DO UPDATE
                SET successes = @s, failures = @f, status = @status, updated_at = now()
            """, conn);
        cmd.Parameters.AddWithValue("id", characterId);
        cmd.Parameters.AddWithValue("s", successes);
        cmd.Parameters.AddWithValue("f", failures);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DeleteDeathSaveAsync(Guid characterId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM death_save_states WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(int Successes, int Failures, string Status)?> ReadDeathSaveAsync(Guid characterId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT successes, failures, status FROM death_save_states WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2));
    }

    private static async Task<int> ReadCurrentHpAsync(Guid characterId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT current_hp FROM characters WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task SetCurrentHpAsync(Guid characterId, int hp)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE characters SET current_hp = @hp WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        cmd.Parameters.AddWithValue("hp", hp);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountDiceRollsAsync(Guid characterId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── url helpers ─────────────────────────────────────────────────────────

    private string StateUrl(Guid characterId, Guid? campaignId = null) =>
        $"/campaigns/{campaignId ?? _campaignId}/characters/{characterId}/death-saves";

    private string ConfirmUrl(Guid characterId, Guid? campaignId = null) =>
        $"{StateUrl(characterId, campaignId)}/confirm-dying";

    private string RollUrl(Guid characterId, Guid? campaignId = null, Guid? sessionId = null) =>
        $"/campaigns/{campaignId ?? _campaignId}/sessions/{sessionId ?? _sessionId}" +
        $"/characters/{characterId}/death-saves/roll";

    private string AttackUrl(Guid attackerId) =>
        $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{attackerId}/attacks";

    // ════════════════════════════════════════════════════════════════════════
    // POST confirm-dying
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Confirm_dying_without_token_is_401()
    {
        var response = await _factory.CreateClient().PostAsync(ConfirmUrl(_zeroHpCharacterId), null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dm_can_confirm_dying_for_character_at_zero_hp()
    {
        await DeleteDeathSaveAsync(_zeroHpCharacterId);

        var response = await AuthedClient(_dmToken).PostAsync(ConfirmUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DeathSaveStateResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(_zeroHpCharacterId, body!.CharacterId);
        Assert.Equal(0, body.Successes);
        Assert.Equal(0, body.Failures);
        Assert.Equal("Rolling", body.Status);

        var row = await ReadDeathSaveAsync(_zeroHpCharacterId);
        Assert.NotNull(row);
        Assert.Equal((0, 0, "Rolling"), row!.Value);
    }

    [Fact]
    public async Task Confirm_dying_by_character_owner_who_is_not_dm_is_403()
    {
        await DeleteDeathSaveAsync(_zeroHpCharacterId);

        // playerA เป็นเจ้าของ _zeroHpCharacterId แต่ decision ล็อกว่า "เข้าสู่สถานะกำลังตาย" เป็นสิทธิ์ DM
        var response = await AuthedClient(_playerAToken).PostAsync(ConfirmUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await ReadDeathSaveAsync(_zeroHpCharacterId));
    }

    [Fact]
    public async Task Confirm_dying_by_non_member_is_404()
    {
        var response = await AuthedClient(_outsiderToken).PostAsync(ConfirmUrl(_zeroHpCharacterId), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_dying_for_character_of_another_campaign_is_404()
    {
        // DM ของแคมเปญนี้ ส่ง characterId ของโต๊ะอื่นมา — ต้อง 404 ไม่ใช่ 403 (กัน enumeration)
        var response = await AuthedClient(_dmToken).PostAsync(ConfirmUrl(_otherCharacterId), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await ReadDeathSaveAsync(_otherCharacterId));
    }

    [Fact]
    public async Task Confirm_dying_for_character_above_zero_hp_is_400()
    {
        await DeleteDeathSaveAsync(_healthyCharacterId);

        var response = await AuthedClient(_dmToken).PostAsync(ConfirmUrl(_healthyCharacterId), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await ReadDeathSaveAsync(_healthyCharacterId));
    }

    [Theory]
    [InlineData("Rolling")]
    [InlineData("Stable")]
    [InlineData("Dead")]
    public async Task Confirm_dying_when_state_already_exists_is_409(string existingStatus)
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 1, 1, existingStatus);

        var response = await AuthedClient(_dmToken).PostAsync(ConfirmUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // ห้ามรีเซ็ตตัวนับของเดิมทิ้งตอนปฏิเสธ
        var row = await ReadDeathSaveAsync(_zeroHpCharacterId);
        Assert.Equal((1, 1, existingStatus), row!.Value);
    }

    // ════════════════════════════════════════════════════════════════════════
    // POST roll
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Roll_without_token_is_401()
    {
        var response = await _factory.CreateClient().PostAsync(RollUrl(_zeroHpCharacterId), null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Roll_when_no_death_save_state_exists_is_409()
    {
        await DeleteDeathSaveAsync(_zeroHpCharacterId);

        var response = await AuthedClient(_playerAToken).PostAsync(RollUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(await ReadDeathSaveAsync(_zeroHpCharacterId));
    }

    [Theory]
    [InlineData("Stable")]
    [InlineData("Dead")]
    public async Task Roll_when_state_is_not_rolling_is_409(string status)
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 3, 0, status);

        var response = await AuthedClient(_playerAToken).PostAsync(RollUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Roll_by_member_who_is_neither_owner_nor_controlling_dm_is_403()
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 0, "Rolling");

        // playerB เป็นสมาชิกแคมเปญ แต่ไม่ใช่เจ้าของ _zeroHpCharacterId
        var response = await AuthedClient(_playerBToken).PostAsync(RollUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal((0, 0, "Rolling"), (await ReadDeathSaveAsync(_zeroHpCharacterId))!.Value);
    }

    [Fact]
    public async Task Roll_by_dm_for_player_owned_character_is_403()
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 0, "Rolling");

        // DM override มีได้เฉพาะตัวละคร ControlledByDm เท่านั้น (RequireCharacterActorAsync)
        var response = await AuthedClient(_dmToken).PostAsync(RollUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Roll_by_non_member_is_404()
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 0, "Rolling");

        var response = await AuthedClient(_outsiderToken).PostAsync(RollUrl(_zeroHpCharacterId), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Roll_with_session_from_another_campaign_is_404()
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 0, "Rolling");

        var response = await AuthedClient(_playerAToken).PostAsync(
            RollUrl(_zeroHpCharacterId, sessionId: _otherSessionId), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Roll_by_dm_for_dm_controlled_character_is_allowed()
    {
        await UpsertDeathSaveAsync(_dmControlledCharacterId, 0, 0, "Rolling");

        var response = await AuthedClient(_dmToken).PostAsync(RollUrl(_dmControlledCharacterId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Roll_when_campaign_dice_mode_is_dm_entered_is_400()
    {
        await UpsertDeathSaveAsync(_dmEnteredCharacterId, 0, 0, "Rolling");

        var response = await AuthedClient(_playerAToken).PostAsync(
            RollUrl(_dmEnteredCharacterId, _dmEnteredCampaignId, _dmEnteredSessionId), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0, "Rolling"), (await ReadDeathSaveAsync(_dmEnteredCharacterId))!.Value);
    }

    [Fact]
    public async Task Roll_persists_a_dice_roll_row_and_never_leaks_the_seed()
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 0, "Rolling");
        var before = await CountDiceRollsAsync(_zeroHpCharacterId);

        var response = await AuthedClient(_playerAToken).PostAsync(RollUrl(_zeroHpCharacterId), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("seed", raw, StringComparison.OrdinalIgnoreCase);

        var body = JsonSerializer.Deserialize<DeathSaveRollResponse>(raw, JsonOptions)!;
        Assert.Equal("1d20", body.Expression);
        Assert.NotEqual(Guid.Empty, body.Id);

        // ทอยเดี่ยว d20 หนึ่งลูกเสมอ ไม่มี drop
        var kept = Assert.Single(body.Rolls.Where(r => r is { Sides: 20, Dropped: false }));
        Assert.InRange(kept.Value, 1, 20);
        Assert.Equal(kept.Value, body.Total);

        Assert.Equal(before + 1, await CountDiceRollsAsync(_zeroHpCharacterId));
    }

    /// <summary>
    /// property-based: ทอยจากสถานะสด (0/0 Rolling) ซ้ำหลายรอบ แล้วยืนยันว่าผลลัพธ์ที่ตอบกลับ
    /// สอดคล้องกับ**ค่า d20 ที่ตอบกลับมาเอง**ทุกครั้ง (ไม่พึ่งโชคว่าจะออกเลขอะไร) — ครอบทั้ง 4 outcome
    /// ตามที่เต๋าพาไป และยืนยัน side effect ที่ต่างกันของ nat20 (ฟื้น 1 HP + ล้างสถานะ)
    /// </summary>
    [Fact]
    public async Task Roll_outcome_counters_and_hp_always_match_the_rolled_d20_value()
    {
        var client = AuthedClient(_playerAToken);
        var seenOutcomes = new HashSet<string>();

        for (var i = 0; i < 60; i++)
        {
            await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 0, "Rolling");
            await SetCurrentHpAsync(_zeroHpCharacterId, 0);

            var response = await client.PostAsync(RollUrl(_zeroHpCharacterId), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<DeathSaveRollResponse>(JsonOptions))!;

            var value = body.Rolls.Single(r => r is { Sides: 20, Dropped: false }).Value;
            var row = await ReadDeathSaveAsync(_zeroHpCharacterId);
            var hp = await ReadCurrentHpAsync(_zeroHpCharacterId);
            seenOutcomes.Add(body.Outcome);

            if (value == 20)
            {
                Assert.Equal("Revived", body.Outcome);
                Assert.True(body.Revived);
                Assert.Equal("NotDying", body.Status);
                Assert.Equal(0, body.Successes);
                Assert.Equal(0, body.Failures);
                Assert.Equal(1, body.CurrentHp);
                Assert.Equal(1, hp);
                // ฟื้นแล้ว = ไม่มีสถานะ death save ค้างอยู่อีก
                Assert.Null(row);
            }
            else if (value == 1)
            {
                Assert.Equal("CriticalFailure", body.Outcome);
                Assert.False(body.Revived);
                Assert.Equal(0, body.Successes);
                Assert.Equal(2, body.Failures);
                Assert.Equal("Rolling", body.Status);
                Assert.Equal(0, hp);
                Assert.Equal((0, 2, "Rolling"), row!.Value);
            }
            else if (value >= 10)
            {
                Assert.Equal("Success", body.Outcome);
                Assert.Equal(1, body.Successes);
                Assert.Equal(0, body.Failures);
                Assert.Equal("Rolling", body.Status);
                Assert.Equal((1, 0, "Rolling"), row!.Value);
            }
            else
            {
                Assert.Equal("Failure", body.Outcome);
                Assert.Equal(0, body.Successes);
                Assert.Equal(1, body.Failures);
                Assert.Equal("Rolling", body.Status);
                Assert.Equal((0, 1, "Rolling"), row!.Value);
            }
        }

        // 60 รอบยังไม่เจอทั้ง success และ failure เลยแปลว่าเต๋าพัง ไม่ใช่โชคร้าย
        // (P(ไม่เจอ success เลย) = 0.55^60 ≈ 5e-16)
        Assert.Contains("Success", seenOutcomes);
        Assert.True(seenOutcomes.Contains("Failure") || seenOutcomes.Contains("CriticalFailure"));
    }

    [Fact]
    public async Task Third_success_makes_character_stable_and_stops_further_rolls()
    {
        var client = AuthedClient(_playerAToken);

        // เริ่มที่ 2 success — ทอยจนกว่าจะได้ success อีกครั้ง (10+) แล้วต้องกลายเป็น Stable
        for (var i = 0; i < 60; i++)
        {
            await UpsertDeathSaveAsync(_zeroHpCharacterId, 2, 0, "Rolling");
            var response = await client.PostAsync(RollUrl(_zeroHpCharacterId), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<DeathSaveRollResponse>(JsonOptions))!;
            var value = body.Rolls.Single(r => r is { Sides: 20, Dropped: false }).Value;

            if (value is >= 10 and < 20)
            {
                Assert.Equal(3, body.Successes);
                Assert.Equal("Stable", body.Status);
                Assert.Equal((3, 0, "Stable"), (await ReadDeathSaveAsync(_zeroHpCharacterId))!.Value);

                // Stable แล้วทอยต่อไม่ได้
                var again = await client.PostAsync(RollUrl(_zeroHpCharacterId), null);
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
                return;
            }
        }

        Assert.Fail("ทอย 60 ครั้งไม่เจอค่า 10-19 เลย — เต๋าน่าจะพัง");
    }

    [Fact]
    public async Task Third_failure_marks_character_dead_and_stops_further_rolls()
    {
        var client = AuthedClient(_playerAToken);

        for (var i = 0; i < 60; i++)
        {
            await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 2, "Rolling");
            var response = await client.PostAsync(RollUrl(_zeroHpCharacterId), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<DeathSaveRollResponse>(JsonOptions))!;
            var value = body.Rolls.Single(r => r is { Sides: 20, Dropped: false }).Value;

            if (value is >= 2 and <= 9)
            {
                Assert.Equal(3, body.Failures);
                Assert.Equal("Dead", body.Status);
                Assert.Equal((0, 3, "Dead"), (await ReadDeathSaveAsync(_zeroHpCharacterId))!.Value);

                var again = await client.PostAsync(RollUrl(_zeroHpCharacterId), null);
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
                return;
            }
        }

        Assert.Fail("ทอย 60 ครั้งไม่เจอค่า 2-9 เลย — เต๋าน่าจะพัง");
    }

    /// <summary>
    /// **ช่องโหว่ business-logic ที่ security review จับได้ (High)** — endpoint roll อ่าน
    /// <c>DeathSaveState</c> แล้วเขียนกลับเป็นคนละ round-trip โดยไม่มี concurrency token ⇒ ยิงสอง
    /// request roll พร้อมกันสำหรับตัวละครเดียวกัน ทั้งคู่จะอ่าน state ตั้งต้นเดียวกัน (เช่น failures=2)
    /// แล้วเขียนทับกัน — ผลของอีกฝั่งหายไปเงียบ ๆ ไม่มี error (เช่น การตายถูกทับด้วยผลทอยที่มาทีหลัง)
    ///
    /// **สิ่งที่เทสต์นี้ยืนยันคือ invariant ไม่ใช่จำนวน 200** — ต้องมี**อย่างน้อยหนึ่ง**ฝั่งได้ 409
    /// (concurrency token จับ conflict ได้) ไม่ใช่ 200 ทั้งคู่ (ซึ่งแปลว่าฝั่งหนึ่งเขียนทับอีกฝั่งแบบเงียบ ๆ)
    /// และ state สุดท้ายใน DB ต้องตรงกับผลของฝั่งที่ตอบ 200 เท่านั้น ไม่ใช่ค่าผสม/ค่าขยะ
    ///
    /// วนหลายรอบเพราะ race ไม่ได้เกิดทุกครั้ง (ขึ้นกับ timing ของ scheduler) — reset state กลับเป็น
    /// failures=2 ก่อนทุกรอบเพื่อเปิดหน้าต่างที่ผลลัพธ์มีนัยสำคัญ (ใกล้ตาย)
    /// </summary>
    [Fact]
    public async Task Concurrent_rolls_for_the_same_character_never_silently_lose_an_update()
    {
        var client = AuthedClient(_playerAToken);
        var sawConflict = false;

        for (var round = 0; round < 20 && !sawConflict; round++)
        {
            await UpsertDeathSaveAsync(_zeroHpCharacterId, 0, 2, "Rolling");

            var task1 = client.PostAsync(RollUrl(_zeroHpCharacterId), null);
            var task2 = client.PostAsync(RollUrl(_zeroHpCharacterId), null);
            var responses = await Task.WhenAll(task1, task2);

            foreach (var response in responses)
            {
                Assert.True(
                    response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    $"รอบ {round}: ได้ status ที่ไม่ควรเกิด {(int)response.StatusCode}");
            }

            var okResponses = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
            Assert.True(okResponses.Count is 1 or 2, $"รอบ {round}: ไม่มีฝั่งไหนสำเร็จเลย");

            if (responses.Any(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                sawConflict = true;

                // ฝั่งที่ชนะต้องมีแค่ฝั่งเดียว และ state สุดท้ายต้องตรงกับผลของฝั่งที่ชนะเท่านั้น
                Assert.Single(okResponses);
                var winnerBody = (await okResponses[0].Content
                    .ReadFromJsonAsync<DeathSaveRollResponse>(JsonOptions))!;
                var finalRow = await ReadDeathSaveAsync(_zeroHpCharacterId);

                if (winnerBody.Status == "NotDying")
                {
                    // nat20 ของฝั่งที่ชนะลบแถวทิ้ง
                    Assert.Null(finalRow);
                }
                else
                {
                    Assert.NotNull(finalRow);
                    Assert.Equal(
                        (winnerBody.Successes, winnerBody.Failures, winnerBody.Status), finalRow!.Value);
                }
            }
        }

        Assert.True(sawConflict, "20 รอบไม่เจอ 409 เลย — concurrency token อาจไม่ทำงาน (race ไม่ถูกจับ)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GET death-saves — visibility
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_state_when_no_row_exists_returns_200_not_dying()
    {
        await DeleteDeathSaveAsync(_zeroHpCharacterId);

        var response = await AuthedClient(_playerAToken).GetAsync(StateUrl(_zeroHpCharacterId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<DeathSaveStateResponse>(JsonOptions))!;
        Assert.Equal(_zeroHpCharacterId, body.CharacterId);
        Assert.Equal("NotDying", body.Status);
        Assert.Equal(0, body.Successes);
        Assert.Equal(0, body.Failures);
    }

    [Fact]
    public async Task Get_state_in_party_visible_campaign_is_readable_by_any_member()
    {
        await UpsertDeathSaveAsync(_zeroHpCharacterId, 1, 2, "Rolling");

        // playerB ไม่ใช่เจ้าของตัวละครนี้ แต่แคมเปญตั้ง PartyVisible
        var response = await AuthedClient(_playerBToken).GetAsync(StateUrl(_zeroHpCharacterId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<DeathSaveStateResponse>(JsonOptions))!;
        Assert.Equal(1, body.Successes);
        Assert.Equal(2, body.Failures);
        Assert.Equal("Rolling", body.Status);
    }

    [Fact]
    public async Task Get_state_in_dm_only_campaign_is_403_for_non_dm_member_even_the_owner()
    {
        await UpsertDeathSaveAsync(_dmOnlyCharacterId, 0, 1, "Rolling");

        var response = await AuthedClient(_playerAToken).GetAsync(
            StateUrl(_dmOnlyCharacterId, _dmOnlyCampaignId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_state_in_dm_only_campaign_is_readable_by_dm()
    {
        await UpsertDeathSaveAsync(_dmOnlyCharacterId, 0, 1, "Rolling");

        var response = await AuthedClient(_dmToken).GetAsync(StateUrl(_dmOnlyCharacterId, _dmOnlyCampaignId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<DeathSaveStateResponse>(JsonOptions))!;
        Assert.Equal(1, body.Failures);
    }

    [Fact]
    public async Task Get_state_by_non_member_is_404()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync(StateUrl(_zeroHpCharacterId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_state_for_character_of_another_campaign_is_404()
    {
        var response = await AuthedClient(_playerAToken).GetAsync(StateUrl(_otherCharacterId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CombatEndpoints integration — โดนตีตอน 0 HP / massive damage
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚠️ damage expression ทุกตัวในไฟล์นี้ต้องมีกลุ่มลูกเต๋าจริง (XdY) — `DiceExpressionParser.Parse`
    /// ปฏิเสธ expression ที่เป็นตัวเลขล้วน ("20") ด้วย "ต้องมีอย่างน้อยหนึ่งกลุ่มลูกเต๋า" (บรรทัด 139-142
    /// ของ DiceExpressionParser.cs) → endpoint ตอบ 400 · จึงใช้รูป "2d2+N" ที่ช่วงผลรวมแคบพอจะการันตี
    /// พฤติกรรมที่ต้องการได้ **ทั้งตอน hit ปกติและ critical** (critical คูณเฉพาะจำนวนลูกเต๋า ไม่คูณ flat)
    /// </summary>
    private async Task<AttackResolutionBody> AttackAsync(Guid targetId, string damageExpression)
    {
        var response = await AuthedClient(_playerAToken).PostAsJsonAsync(AttackUrl(_attackerCharacterId), new
        {
            Expression = "1d20+50",   // +50 การันตีว่าไม่มีทางพลาด AC 1 (ยกเว้น nat1 = automatic miss)
            TargetCharacterId = targetId,
            DamageComponents = new[] { new { Expression = damageExpression, Type = "Slashing" } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttackResolutionBody>(JsonOptions))!;
    }

    /// <summary>
    /// โดนโจมตีตอนอยู่ที่ 0 HP อยู่แล้วและกำลัง Rolling → auto-failure ทันทีโดยไม่ต้องทอย
    /// (1 ปกติ / 2 ถ้า critical) · nat1 = พลาด ไม่นับ failure ใด ๆ — วนจนกว่าจะเจอ hit จริง
    /// </summary>
    [Fact]
    public async Task Attack_on_character_already_at_zero_hp_adds_auto_failure()
    {
        for (var i = 0; i < 40; i++)
        {
            await UpsertDeathSaveAsync(_dyingTargetCharacterId, 0, 0, "Rolling");
            await SetCurrentHpAsync(_dyingTargetCharacterId, 0);

            var body = await AttackAsync(_dyingTargetCharacterId, "2d2+2");
            var row = await ReadDeathSaveAsync(_dyingTargetCharacterId);

            if (!body.Hit)
            {
                // automatic miss (nat1) — ห้ามนับ failure
                Assert.Equal((0, 0, "Rolling"), row!.Value);
                continue;
            }

            var expectedFailures = body.Outcome == "CriticalHit" ? 2 : 1;
            Assert.Equal((0, expectedFailures, "Rolling"), row!.Value);
            return;
        }

        Assert.Fail("โจมตี 40 ครั้งไม่โดนเลย — น่าจะมีอะไรผิด");
    }

    [Fact]
    public async Task Attack_on_character_at_zero_hp_without_confirmed_dying_state_creates_nothing()
    {
        await DeleteDeathSaveAsync(_dyingTargetCharacterId);
        await SetCurrentHpAsync(_dyingTargetCharacterId, 0);

        await AttackAsync(_dyingTargetCharacterId, "2d2+2");

        // decision ล็อกไว้: ต้องให้ DM confirm ก่อนเท่านั้น ระบบห้ามสร้างสถานะเอง
        Assert.Null(await ReadDeathSaveAsync(_dyingTargetCharacterId));
    }

    [Fact]
    public async Task Fresh_drop_to_zero_hp_does_not_create_death_save_state()
    {
        await DeleteDeathSaveAsync(_healthyCharacterId);
        await SetCurrentHpAsync(_healthyCharacterId, 20);

        for (var i = 0; i < 40; i++)
        {
            var body = await AttackAsync(_healthyCharacterId, "2d2+18");
            if (!body.Hit)
            {
                await SetCurrentHpAsync(_healthyCharacterId, 20);
                continue;
            }

            Assert.Equal(0, body.TargetNewHp);
            Assert.True(body.TargetDroppedToZero);
            Assert.False(body.TargetIsInstantDeath);
            Assert.Null(await ReadDeathSaveAsync(_healthyCharacterId));
            return;
        }

        Assert.Fail("โจมตี 40 ครั้งไม่โดนเลย — น่าจะมีอะไรผิด");
    }

    /// <summary>massive damage (เกิน max HP หลังตกถึง 0) = ตายทันทีโดยไม่ต้องให้ DM confirm</summary>
    [Fact]
    public async Task Massive_damage_instant_death_sets_status_dead_without_confirmation()
    {
        await DeleteDeathSaveAsync(_healthyCharacterId);

        for (var i = 0; i < 40; i++)
        {
            await SetCurrentHpAsync(_healthyCharacterId, 20);
            await DeleteDeathSaveAsync(_healthyCharacterId);

            var body = await AttackAsync(_healthyCharacterId, "2d2+40"); // 45 - 20 = 25 ส่วนเกิน >= maxHp 20
            if (!body.Hit)
            {
                continue;
            }

            Assert.True(body.TargetIsInstantDeath);
            var row = await ReadDeathSaveAsync(_healthyCharacterId);
            Assert.NotNull(row);
            Assert.Equal("Dead", row!.Value.Status);
            return;
        }

        Assert.Fail("โจมตี 40 ครั้งไม่โดนเลย — น่าจะมีอะไรผิด");
    }

    /// <summary>instant death ต้องมาก่อน auto-failure เสมอ — ตัวที่กำลัง Rolling อยู่แล้วโดน massive
    /// damage ต้องกลายเป็น Dead ไม่ใช่แค่ +1/+2 failure</summary>
    [Fact]
    public async Task Instant_death_takes_priority_over_auto_failure_for_dying_character()
    {
        for (var i = 0; i < 40; i++)
        {
            await UpsertDeathSaveAsync(_dyingTargetCharacterId, 0, 0, "Rolling");
            await SetCurrentHpAsync(_dyingTargetCharacterId, 0);

            var body = await AttackAsync(_dyingTargetCharacterId, "2d2+20"); // 0 HP อยู่แล้ว → ส่วนเกิน 25 >= maxHp 20
            if (!body.Hit)
            {
                continue;
            }

            Assert.True(body.TargetIsInstantDeath);
            var row = await ReadDeathSaveAsync(_dyingTargetCharacterId);
            Assert.Equal("Dead", row!.Value.Status);
            return;
        }

        Assert.Fail("โจมตี 40 ครั้งไม่โดนเลย — น่าจะมีอะไรผิด");
    }

    [Fact]
    public async Task Auto_failure_reaching_three_marks_character_dead()
    {
        for (var i = 0; i < 40; i++)
        {
            await UpsertDeathSaveAsync(_dyingTargetCharacterId, 0, 2, "Rolling");
            await SetCurrentHpAsync(_dyingTargetCharacterId, 0);

            var body = await AttackAsync(_dyingTargetCharacterId, "2d2+2");
            if (!body.Hit)
            {
                continue;
            }

            var row = await ReadDeathSaveAsync(_dyingTargetCharacterId);
            Assert.Equal((0, 3, "Dead"), row!.Value);
            return;
        }

        Assert.Fail("โจมตี 40 ครั้งไม่โดนเลย — น่าจะมีอะไรผิด");
    }

    [Fact]
    public async Task Attack_on_stable_character_at_zero_hp_does_not_add_failure()
    {
        // Stable = พ้นอันตรายแล้ว · ApplyAttackedAtZeroHp โยน exception ถ้าไม่ใช่ Rolling
        // endpoint ต้องไม่เรียกมันเลย ไม่ใช่เรียกแล้วปล่อย 500
        for (var i = 0; i < 40; i++)
        {
            await UpsertDeathSaveAsync(_dyingTargetCharacterId, 3, 0, "Stable");
            await SetCurrentHpAsync(_dyingTargetCharacterId, 0);

            var body = await AttackAsync(_dyingTargetCharacterId, "2d2+2");
            if (!body.Hit)
            {
                continue;
            }

            Assert.Equal((3, 0, "Stable"), (await ReadDeathSaveAsync(_dyingTargetCharacterId))!.Value);
            return;
        }

        Assert.Fail("โจมตี 40 ครั้งไม่โดนเลย — น่าจะมีอะไรผิด");
    }

    // ── response shapes ─────────────────────────────────────────────────────

    private record AuthTokenResponse(string Token, Guid UserId);

    private record RollDetailBody(int Sides, int Value, bool Dropped, int Sign);

    private record DeathSaveStateResponse(Guid CharacterId, int Successes, int Failures, string Status);

    private record DeathSaveRollResponse(
        Guid Id,
        Guid CharacterId,
        string Expression,
        List<RollDetailBody> Rolls,
        int Total,
        string Outcome,
        int Successes,
        int Failures,
        string Status,
        bool Revived,
        int CurrentHp);

    private record AttackResolutionBody(
        string Outcome,
        bool Hit,
        int? TargetNewHp,
        bool? TargetDroppedToZero,
        bool? TargetIsInstantDeath);
}

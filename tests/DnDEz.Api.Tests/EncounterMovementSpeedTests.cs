using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 8 — ระยะเคลื่อนที่ต่อเทิร์นต้องมาจาก `characters.speed_feet` ของตัวละครที่ถึงตาเดิน
/// ไม่ใช่ค่าคงที่ 30 ที่ฝังไว้ใน <c>EncounterEndpoints</c>
///
/// ค่า 30 เดิมถูกใส่ไว้ตั้งแต่ Sprint F งานที่ 4 พร้อม comment ยอมรับว่าชั่วคราวเพราะตอนนั้นสคีมายัง
/// ไม่มีฟิลด์ speed เลย — Part 1 ของ Sprint G เพิ่ม `speed_feet` แล้ว (ดีฟอลต์ 30) Part นี้จึงเป็นรอบ
/// ที่ต้องเลิกฝังค่า
///
/// **ทำไมต้องอ่านสดทุกเทิร์น ไม่ใช่ snapshot ตอนเข้า encounter** — ต่างจาก `initiative_modifier`
/// ที่จงใจ snapshot ไว้บน `encounter_participants` (เพื่อไม่ให้ลำดับเทิร์นสลับกลางการต่อสู้):
/// `ActionEconomyResolver.StartTurn` เขียนกำกับไว้เองว่ารับ maxMovementFeet ใหม่ทุกครั้ง *"เพราะ
/// speed อาจเปลี่ยนจาก effect ระหว่างเกม (เช่น slow/haste/grapple ทำ speed=0)"* — speed จึงเป็นค่า
/// ที่ต้องอ่านสดตอนขึ้นเทิร์น ไม่ใช่แช่ไว้
///
/// เทสต์นี้แยกไฟล์จาก <c>EncounterEndpointsTests.cs</c> โดยตั้งใจ เพราะไฟล์นั้นประกาศตัวเองว่าเป็น
/// contract ที่ห้ามแก้ และตัวละครใน fixture ของมันใช้ speed ดีฟอลต์ (30) ทั้งหมด — เทสต์เดิมทุกตัว
/// จึงยังเขียวเหมือนเดิมหลังเปลี่ยนมาอ่านค่าจริง ซึ่งเป็นการยืนยันว่าไม่มี regression ในตัวเอง
/// </summary>
public class EncounterMovementSpeedTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "encounter-speed-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    /// <summary>initiative +20 → ได้เทิร์นแรกเสมอ · speed 40 (เช่น monk) ไม่ใช่ 30</summary>
    private readonly Guid _swiftCharacterId = Guid.NewGuid();

    /// <summary>initiative 0 → เทิร์นที่สอง · speed 25 (เช่น dwarf) ไม่ใช่ 30</summary>
    private readonly Guid _stoutCharacterId = Guid.NewGuid();

    /// <summary>initiative -20 → เทิร์นสุดท้าย · speed 0 (โดนจับล็อก/อัมพาต)</summary>
    private readonly Guid _rootedCharacterId = Guid.NewGuid();

    private const int SwiftSpeed = 40;
    private const int StoutSpeed = 25;

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Speed Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        await InsertCharacterAsync(conn, _swiftCharacterId, "Swift", initiativeModifier: 20, speedFeet: SwiftSpeed);
        await InsertCharacterAsync(conn, _stoutCharacterId, "Stout", initiativeModifier: 0, speedFeet: StoutSpeed);
        await InsertCharacterAsync(conn, _rootedCharacterId, "Rooted", initiativeModifier: -20, speedFeet: 0);
    }

    /// <summary>
    /// เทิร์นแรกเป็นของ Swift (speed 40) — ระยะที่ได้ต้องเป็น 40 ไม่ใช่ 30
    ///
    /// ถ้ายังฝัง 30 อยู่ เทสต์นี้จะได้ 30 แล้วตก — และนั่นคือบั๊กที่ผู้เล่นสาย monk เจอจริง คือ
    /// เดินได้สั้นกว่าที่ character sheet ของตัวเองเขียนไว้ โดยไม่มีอะไรอธิบายว่าทำไม
    /// </summary>
    [Fact]
    public async Task Starting_encounter_uses_the_first_characters_own_speed()
    {
        var encounter = await StartEncounterAsync();

        Assert.Equal(_swiftCharacterId, encounter.CurrentCharacterId);
        Assert.Equal(SwiftSpeed, encounter.TurnState!.MaxMovementFeet);
        Assert.Equal(SwiftSpeed, encounter.TurnState.MovementRemainingFeet);
    }

    /// <summary>ขึ้นเทิร์นถัดไปแล้วงบต้องเปลี่ยนตามเจ้าของเทิร์นใหม่ (40 → 25) ไม่ใช่ค้างค่าเดิม
    /// หรือกลับไปเป็น 30</summary>
    [Fact]
    public async Task Next_turn_switches_budget_to_the_next_characters_speed()
    {
        var encounter = await StartEncounterAsync();

        var response = await AuthedClient(_dmToken)
            .PostAsync($"/campaigns/{_campaignId}/encounters/{encounter.Id}/next-turn", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<NextTurnResponse>(JsonOptions);

        Assert.Equal(_stoutCharacterId, body!.CurrentCharacterId);
        Assert.Equal(StoutSpeed, body.TurnState.MaxMovementFeet);
        Assert.Equal(StoutSpeed, body.TurnState.MovementRemainingFeet);
    }

    /// <summary>
    /// speed 0 (ถูกจับล็อก/อัมพาต) ต้องได้ระยะ 0 — ไม่ใช่ตกกลับไปเป็นดีฟอลต์ 30
    ///
    /// เป็นเคสที่ค่าดีฟอลต์แบบฝังตายอันตรายที่สุด: ตัวละครที่ "ขยับไม่ได้" กลับเดินได้เต็มระยะ
    /// ซึ่งพลิกผลการต่อสู้ได้ทั้งยก · `ActionEconomyResolver.StartTurn(0)` รองรับ 0 อยู่แล้ว
    /// (มีเทสต์ StartTurn_ZeroMaxMovement_AllowedAndFullyRestricted คุมไว้) จึงไม่ต้องกันเป็นกรณีพิเศษ
    /// </summary>
    [Fact]
    public async Task Character_with_zero_speed_gets_no_movement_budget()
    {
        var encounter = await StartEncounterAsync();
        var client = AuthedClient(_dmToken);

        // ข้ามไปเทิร์นที่สาม (Rooted) — เรียง initiative +20 / 0 / -20 จึงแน่นอนเสมอ
        await client.PostAsync($"/campaigns/{_campaignId}/encounters/{encounter.Id}/next-turn", null);
        var response = await client.PostAsync(
            $"/campaigns/{_campaignId}/encounters/{encounter.Id}/next-turn", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<NextTurnResponse>(JsonOptions);

        Assert.Equal(_rootedCharacterId, body!.CurrentCharacterId);
        Assert.Equal(0, body.TurnState.MaxMovementFeet);
        Assert.Equal(0, body.TurnState.MovementRemainingFeet);
    }

    /// <summary>อ่าน encounter ที่ active อยู่ก็ต้องได้งบของเจ้าของเทิร์นปัจจุบันเช่นกัน — จุดนี้เป็นทาง
    /// อ่านคนละเส้นกับตอนเปิด/ขึ้นเทิร์น (มี fallback ตอน Redis หาย) จึงต้องคุมแยก ไม่งั้นจะเหลือ 30
    /// ฝังอยู่เฉพาะเส้นทางนี้เส้นเดียวแล้วไม่มีใครเห็น</summary>
    [Fact]
    public async Task Active_encounter_reports_the_current_characters_speed()
    {
        await StartEncounterAsync();

        var response = await AuthedClient(_dmToken)
            .GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/encounters/active");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EncounterResponse>(JsonOptions);

        Assert.Equal(_swiftCharacterId, body!.CurrentCharacterId);
        Assert.Equal(SwiftSpeed, body.TurnState!.MaxMovementFeet);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<EncounterResponse> StartEncounterAsync()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/encounters",
            new { CharacterIds = new[] { _swiftCharacterId, _stoutCharacterId, _rootedCharacterId } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EncounterResponse>(JsonOptions))!;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"encounter-speed-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Encounter Speed {label}",
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

    private async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, string name, int initiativeModifier, int speedFeet)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}', 20, 20, 0, 10, @initiativeModifier,
                 @speedFeet, ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", _dmUserId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("initiativeModifier", initiativeModifier);
        cmd.Parameters.AddWithValue("speedFeet", speedFeet);
        await cmd.ExecuteNonQueryAsync();
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

        await using (var deleteUser = new NpgsqlCommand("DELETE FROM users WHERE id = @id", conn))
        {
            deleteUser.Parameters.AddWithValue("id", _dmUserId);
            await deleteUser.ExecuteNonQueryAsync();
        }

        _factory.Dispose();
    }

    // ── response contracts (นิยามในเทสต์เอง ไม่พึ่ง internal type ของ Api) ──

    private record AuthTokenResponse(string Token, Guid UserId);

    private record TurnStateResponse(
        bool ActionAvailable,
        bool BonusActionAvailable,
        bool ReactionAvailable,
        int MovementRemainingFeet,
        int MaxMovementFeet);

    private record ParticipantResponse(
        Guid Id,
        Guid CharacterId,
        string CharacterName,
        int InitiativeRoll,
        int InitiativeModifier,
        int InitiativeTotal,
        int TurnOrder);

    private record EncounterResponse(
        Guid Id,
        Guid CampaignId,
        Guid SessionId,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        Guid? CurrentParticipantId,
        Guid? CurrentCharacterId,
        int TurnNumber,
        List<ParticipantResponse> Participants,
        TurnStateResponse? TurnState);

    private record NextTurnResponse(
        Guid EncounterId,
        Guid CurrentParticipantId,
        Guid CurrentCharacterId,
        int TurnOrder,
        TurnStateResponse TurnState);
}

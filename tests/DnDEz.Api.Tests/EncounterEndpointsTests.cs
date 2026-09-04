using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StackExchange.Redis;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 4 — encounter / initiative / turn order ผ่าน HTTP จริง + Postgres จริง + Redis จริง
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// POST   /campaigns/{campaignId}/sessions/{sessionId}/encounters                [DM เท่านั้น]
///        body { CharacterIds: [guid, ...] }  (1..20 ตัว ห้ามซ้ำ ต้องอยู่ในแคมเปญนี้)
///        201 Created + EncounterResponse (Participants เรียงตาม TurnOrder แล้ว)
///        400 = list ว่าง/ซ้ำ/เกิน 20 · 403 = สมาชิกที่ไม่ใช่ DM · 404 = ไม่ใช่สมาชิก / session
///        หรือ character ไม่อยู่ในแคมเปญนี้ · 409 = session นี้มี encounter ที่ยัง active อยู่แล้ว
///
/// POST   /campaigns/{campaignId}/encounters/{encounterId}/next-turn             [DM เท่านั้น]
///        200 + NextTurnResponse · 403 non-DM · 404 มองไม่เห็น · 409 encounter จบไปแล้ว
///
/// POST   /campaigns/{campaignId}/encounters/{encounterId}/end                   [DM เท่านั้น]
///        200 + EndEncounterResponse · 409 ถ้าจบไปแล้ว
///
/// POST   /campaigns/{campaignId}/encounters/{encounterId}/turn-actions/spend
///        [DM **หรือ** เจ้าของตัวละครที่กำลังถึงตาเดิน เท่านั้น]
///        body { Resource: "Action"|"BonusAction"|"Reaction"|"Movement", Feet?: int }
///        200 + SpendResponse · 400 = resource ไม่รู้จัก / Movement ไม่ส่ง Feet / **double-spend**
///        (double-spend ต้องเป็น 400 ไม่ใช่ 500 — `InvalidOperationException` จาก
///        `ActionEconomyResolver` เป็น input error ของ caller ไม่ใช่ bug ของ server)
///        403 = เป็นสมาชิกแต่ไม่ใช่ DM และไม่ใช่เจ้าของตัวละครที่ถึงตา · 404 = ไม่ใช่สมาชิก
///        409 = encounter จบแล้ว
///
/// GET    /campaigns/{campaignId}/sessions/{sessionId}/encounters/active         [สมาชิกทุกคน]
///        200 + EncounterResponse · **204 No Content** เมื่อ session นี้ไม่มี encounter ที่ active
///        404 = ไม่ใช่สมาชิก / session ไม่อยู่ในแคมเปญนี้
///
/// **Redis หาย = รีเซ็ตเทิร์นปัจจุบัน ไม่ใช่ 500** (ล็อกไว้ใน PROJECT-CONTEXT §4 คำถามที่ 3) —
/// ดู <see cref="Active_encounter_falls_back_to_full_turn_resources_when_redis_is_unavailable"/>
///
/// การทอย initiative เป็น CSPRNG จริงเหมือนทั้งโปรเจกต์ (ห้าม mock) — เทสต์ที่ต้องการลำดับแน่นอน
/// จึงใช้ `initiative_modifier` ที่ห่างกันจนช่วงผลรวมไม่ทับกันเลย (+20 → 21..40, 0 → 1..20,
/// -20 → -19..0) ไม่ใช่พึ่งโชคจากลูกเต๋า · ส่วนกฎ tie-break เองทดสอบแบบ deterministic แยกไว้ที่
/// <c>InitiativeOrderTests.cs</c> (pure function) เพราะบังคับให้เต๋าเสมอกันผ่าน HTTP ไม่ได้
/// </summary>
public class EncounterEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "encounter-endpoints-secret-1";

    /// <summary>ค่า movement ที่ระบบต้องใช้เป็นดีฟอลต์ — ยังไม่มี speed ในสคีมา (ดูรายงานงานที่ 4)</summary>
    private const int DefaultMovementFeet = 30;

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _emptySessionId = Guid.NewGuid();

    private readonly Guid _fastCharacterId = Guid.NewGuid();  // modifier +20 → ได้ turn_order 0 เสมอ
    private readonly Guid _midCharacterId = Guid.NewGuid();   // modifier   0 → turn_order 1 เสมอ
    private readonly Guid _slowCharacterId = Guid.NewGuid();  // modifier -20 → turn_order 2 เสมอ
    private readonly Guid _ownerlessCharacterId = Guid.NewGuid(); // owner_user_id NULL (quick play/orphan)

    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();
    private readonly Guid _otherCharacterId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerAUserId;   // เจ้าของ _fastCharacterId
    private string _playerAToken = null!;
    private Guid _playerBUserId;   // เจ้าของ _midCharacterId + _slowCharacterId
    private string _playerBToken = null!;
    private Guid _outsiderUserId;  // ไม่ใช่สมาชิกแคมเปญนี้เลย (แต่เป็น DM ของ _otherCampaignId)
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

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Encounter Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _outsiderUserId, $"Other Campaign {_suffix}");

        // DM ต้องมีแถว campaign_members role 'Dm' ของตัวเอง ไม่งั้น RLS กรองแคมเปญทิ้งตอน DM เรียกเอง
        // (fixture insert ตรงเข้า campaigns จึงไม่ได้แถวนี้จาก app_create_campaign() อัตโนมัติ)
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerAUserId, "Player");
        await InsertMembershipAsync(conn, _campaignId, _playerBUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _outsiderUserId, "Dm");

        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _emptySessionId, _campaignId);
        await InsertSessionAsync(conn, _otherSessionId, _otherCampaignId);

        await InsertCharacterAsync(conn, _fastCharacterId, _campaignId, "Fast", 20, _playerAUserId);
        await InsertCharacterAsync(conn, _midCharacterId, _campaignId, "Mid", 0, _playerBUserId);
        await InsertCharacterAsync(conn, _slowCharacterId, _campaignId, "Slow", -20, _playerBUserId);
        await InsertCharacterAsync(conn, _otherCharacterId, _otherCampaignId, "Outsider PC", 0, _outsiderUserId);
        await InsertCharacterAsync(conn, _ownerlessCharacterId, _campaignId, "Ownerless", 0, null);
    }

    // ── fixture helpers ─────────────────────────────────────────────────────

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
        NpgsqlConnection conn, Guid id, Guid campaignId, string name,
        int initiativeModifier, Guid? ownerUserId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}', 20, 20, 0, 10, @initiativeModifier,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("initiativeModifier", initiativeModifier);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _otherCampaignId });
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
            Email = $"encounter-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Encounter {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token) => Authed(_factory, token);

    private static HttpClient Authed(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string EncountersUrl(Guid? sessionId = null) =>
        $"/campaigns/{_campaignId}/sessions/{sessionId ?? _sessionId}/encounters";

    private string ActiveUrl(Guid? sessionId = null) => $"{EncountersUrl(sessionId)}/active";

    private string EncounterUrl(Guid encounterId) => $"/campaigns/{_campaignId}/encounters/{encounterId}";

    /// <summary>
    /// factory ที่สลับ <c>IConnectionMultiplexer</c> ใน DI เป็นตัวที่ชี้พอร์ตที่ไม่มีอะไรฟังอยู่ —
    /// `abortConnect=false` ทำให้ `Connect()` ไม่ throw ตอนสร้าง แต่ไปพังตอนเรียกใช้จริง ซึ่งเป็น
    /// เส้นทางที่ store ต้องจับให้ได้ · จงใจ**ไม่**ใช้ fake ของ store เอง เพราะจะกลายเป็นการเทสต์ fake
    /// แทน catch path จริง (DB ยังเป็นตัวเดิม — สลับเฉพาะ Redis)
    /// </summary>
    private WebApplicationFactory<Program> CreateDeadRedisFactory() =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
                    "localhost:6399,abortConnect=false,connectTimeout=200,connectRetry=0,syncTimeout=200"));
            }));

    /// <summary>เริ่ม encounter มาตรฐาน 3 ตัว (fast/mid/slow) ในฐานะ DM แล้วคืน response ที่ parse แล้ว</summary>
    private async Task<EncounterResponse> StartStandardEncounterAsync()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _slowCharacterId, _fastCharacterId, _midCharacterId },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EncounterResponse>(JsonOptions);
        Assert.NotNull(body);
        return body!;
    }

    // ── POST encounters — สร้าง encounter + ทอย initiative + จัดลำดับ ─────────

    [Fact]
    public async Task Dm_can_start_encounter_and_participants_are_ordered_by_initiative_total_desc()
    {
        var body = await StartStandardEncounterAsync();

        Assert.Equal(_campaignId, body.CampaignId);
        Assert.Equal(_sessionId, body.SessionId);
        Assert.Null(body.EndedAt);
        Assert.Equal(3, body.Participants.Count);

        // ส่ง CharacterIds มาสลับลำดับโดยตั้งใจ — ผลลัพธ์ต้องมาจากผลรวม ไม่ใช่ลำดับที่ส่งเข้ามา
        Assert.Equal(_fastCharacterId, body.Participants[0].CharacterId);
        Assert.Equal(_midCharacterId, body.Participants[1].CharacterId);
        Assert.Equal(_slowCharacterId, body.Participants[2].CharacterId);

        Assert.Equal(new[] { 0, 1, 2 }, body.Participants.Select(p => p.TurnOrder).ToArray());
        Assert.Equal(new[] { 20, 0, -20 }, body.Participants.Select(p => p.InitiativeModifier).ToArray());

        foreach (var participant in body.Participants)
        {
            Assert.InRange(participant.InitiativeRoll, 1, 20);
            Assert.Equal(participant.InitiativeRoll + participant.InitiativeModifier, participant.InitiativeTotal);
        }

        // เทิร์นแรกเป็นของคนที่ turn_order = 0 และทรัพยากรต้องเต็ม
        Assert.Equal(body.Participants[0].Id, body.CurrentParticipantId);
        Assert.Equal(_fastCharacterId, body.CurrentCharacterId);
        AssertFullTurnState(body.TurnState);
    }

    private static void AssertFullTurnState(TurnStateResponse? state)
    {
        Assert.NotNull(state);
        Assert.True(state!.ActionAvailable);
        Assert.True(state.BonusActionAvailable);
        Assert.True(state.ReactionAvailable);
        Assert.Equal(DefaultMovementFeet, state.MaxMovementFeet);
        Assert.Equal(DefaultMovementFeet, state.MovementRemainingFeet);
    }

    /// <summary>audit trail — initiative เป็นการทอยเต๋าจริง ต้องลง `dice_rolls` เหมือน attack/check</summary>
    [Fact]
    public async Task Start_encounter_persists_one_system_dice_roll_per_participant()
    {
        await StartStandardEncounterAsync();

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT character_id, expression, total, source, seed
            FROM dice_rolls
            WHERE session_id = @sessionId
            """, conn);
        cmd.Parameters.AddWithValue("sessionId", _sessionId);

        var seen = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            seen.Add(reader.GetGuid(0));
            Assert.Equal("1d20", reader.GetString(1));
            Assert.InRange(reader.GetInt32(2), 1, 20);   // total ของแถวนี้คือผลเต๋าดิบ ไม่รวม modifier
            Assert.Equal("SystemRoll", reader.GetString(3));
            Assert.False(await reader.IsDBNullAsync(4)); // SystemRoll ต้องมี seed เสมอ (ck_dice_rolls_provenance)
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(
            new[] { _fastCharacterId, _midCharacterId, _slowCharacterId }.OrderBy(x => x).ToArray(),
            seen.OrderBy(x => x).ToArray());
    }

    /// <summary>
    /// เหตุผลที่ต้อง snapshot `initiative_modifier` ลงแถว participant แทนการอ่านสดจาก `characters`:
    /// ถ้าค่าตัวละครเปลี่ยนกลางเกม ลำดับเทิร์นที่ทอยไปแล้วต้องไม่ถูกเรียงใหม่ย้อนหลัง
    /// </summary>
    [Fact]
    public async Task Participant_initiative_modifier_is_snapshotted_not_read_live()
    {
        var started = await StartStandardEncounterAsync();

        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var update = new NpgsqlCommand(
                "UPDATE characters SET initiative_modifier = -20 WHERE id = @id", conn);
            update.Parameters.AddWithValue("id", _fastCharacterId);
            await update.ExecuteNonQueryAsync();
        }

        var active = await GetActiveAsync(_playerAToken);
        Assert.NotNull(active);
        Assert.Equal(_fastCharacterId, active!.Participants[0].CharacterId);
        Assert.Equal(20, active.Participants[0].InitiativeModifier);
        Assert.Equal(started.Participants[0].InitiativeTotal, active.Participants[0].InitiativeTotal);
    }

    [Fact]
    public async Task Member_who_is_not_dm_cannot_start_an_encounter()
    {
        var response = await AuthedClient(_playerAToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _fastCharacterId },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_starting_an_encounter_gets_404_not_403()
    {
        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _fastCharacterId },
        });

        // 403 จะยืนยันกลาย ๆ ว่าแคมเปญ/session นี้มีอยู่จริง (ลำดับ 404-ก่อน-403 เป็น contract)
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Start_encounter_with_a_character_from_another_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _fastCharacterId, _otherCharacterId },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Start_encounter_with_a_session_of_another_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_otherSessionId}/encounters",
            new { CharacterIds = new[] { _fastCharacterId } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Start_encounter_with_empty_character_list_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            EncountersUrl(), new { CharacterIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_encounter_with_duplicate_character_ids_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _fastCharacterId, _fastCharacterId },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Starting_a_second_encounter_while_one_is_active_returns_409()
    {
        await StartStandardEncounterAsync();

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _fastCharacterId },
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_new_encounter_can_be_started_after_the_previous_one_ended()
    {
        var first = await StartStandardEncounterAsync();
        var end = await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(first.Id)}/end", null);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _fastCharacterId },
        });

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    // ── next-turn ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Next_turn_advances_to_the_next_participant_in_turn_order()
    {
        var started = await StartStandardEncounterAsync();

        var response = await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<NextTurnResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(started.Participants[1].Id, body!.CurrentParticipantId);
        Assert.Equal(_midCharacterId, body.CurrentCharacterId);
        Assert.Equal(1, body.TurnOrder);
        AssertFullTurnState(body.TurnState);
    }

    [Fact]
    public async Task Next_turn_wraps_around_to_the_first_participant_after_the_last()
    {
        var started = await StartStandardEncounterAsync();
        var client = AuthedClient(_dmToken);

        // 3 ผู้เข้าร่วม: 0 → 1 → 2 → กลับมา 0
        for (var i = 0; i < 2; i++)
        {
            var step = await client.PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);
            Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        }

        var wrapped = await client.PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);
        Assert.Equal(HttpStatusCode.OK, wrapped.StatusCode);

        var body = await wrapped.Content.ReadFromJsonAsync<NextTurnResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(0, body!.TurnOrder);
        Assert.Equal(started.Participants[0].Id, body.CurrentParticipantId);
        Assert.Equal(_fastCharacterId, body.CurrentCharacterId);
    }

    /// <summary>
    /// defect รอบ B — `turn_number` ถูกนับใน DB มาตั้งแต่งานที่ 4 แต่ไม่เคยส่งออก API ทำให้ UI
    /// แสดง "รอบที่เท่าไร" ไม่ได้ · เทสต์นี้ยึดว่ามันต้องโผล่ใน response และเดินตามจำนวน next-turn
    /// จริง (ไม่ใช่ค้างที่ 0) เพราะ UI คำนวณเลขรอบจากค่านี้หารจำนวนผู้ร่วมต่อสู้
    /// </summary>
    [Fact]
    public async Task Active_encounter_exposes_turn_number_that_advances_with_each_next_turn()
    {
        var started = await StartStandardEncounterAsync();
        Assert.Equal(0, started.TurnNumber);

        var client = AuthedClient(_dmToken);
        for (var i = 0; i < 3; i++)
        {
            var step = await client.PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);
            Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        }

        var active = await GetActiveAsync(_dmToken);
        Assert.NotNull(active);
        // 3 ผู้เข้าร่วม + เดิน 3 เทิร์น = วนครบรอบพอดี → UI ต้องคำนวณได้ว่าเป็น "รอบที่ 2"
        Assert.Equal(3, active!.TurnNumber);
        Assert.Equal(0, active.Participants.Single(p => p.Id == active.CurrentParticipantId).TurnOrder);
    }

    /// <summary>ทรัพยากรของเทิร์นใหม่ต้องเต็มเสมอ — `StartTurn` ใหม่ ไม่ใช่ state ค้างจากเทิร์นก่อน</summary>
    [Fact]
    public async Task Next_turn_resets_turn_resources_for_the_new_participant()
    {
        var started = await StartStandardEncounterAsync();

        var spend = await SpendAsync(_playerAToken, started.Id, "Action");
        Assert.Equal(HttpStatusCode.OK, spend.StatusCode);

        var next = await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);
        var body = await next.Content.ReadFromJsonAsync<NextTurnResponse>(JsonOptions);
        AssertFullTurnState(body!.TurnState);

        var active = await GetActiveAsync(_dmToken);
        AssertFullTurnState(active!.TurnState);
    }

    [Fact]
    public async Task Member_who_is_not_dm_cannot_advance_the_turn()
    {
        var started = await StartStandardEncounterAsync();

        var response = await AuthedClient(_playerAToken).PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_cannot_advance_the_turn_and_gets_404()
    {
        var started = await StartStandardEncounterAsync();

        var response = await AuthedClient(_outsiderToken).PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Next_turn_on_an_unknown_encounter_returns_404()
    {
        var response = await AuthedClient(_dmToken).PostAsync(
            $"{EncounterUrl(Guid.NewGuid())}/next-turn", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Next_turn_on_an_ended_encounter_returns_409()
    {
        var started = await StartStandardEncounterAsync();
        await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/end", null);

        var response = await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── end ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_can_end_an_encounter_and_active_then_returns_204()
    {
        var started = await StartStandardEncounterAsync();

        var response = await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/end", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EndEncounterResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(started.Id, body!.EncounterId);

        var active = await AuthedClient(_dmToken).GetAsync(ActiveUrl());
        Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var read = new NpgsqlCommand("SELECT ended_at FROM encounters WHERE id = @id", conn);
        read.Parameters.AddWithValue("id", started.Id);
        Assert.IsType<DateTime>(await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Member_who_is_not_dm_cannot_end_an_encounter()
    {
        var started = await StartStandardEncounterAsync();

        var response = await AuthedClient(_playerAToken).PostAsync($"{EncounterUrl(started.Id)}/end", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ending_an_already_ended_encounter_returns_409()
    {
        var started = await StartStandardEncounterAsync();
        await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/end", null);

        var again = await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/end", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // ── turn-actions/spend ──────────────────────────────────────────────────

    private Task<HttpResponseMessage> SpendAsync(
        string token, Guid encounterId, string resource, int? feet = null) =>
        AuthedClient(token).PostAsJsonAsync(
            $"{EncounterUrl(encounterId)}/turn-actions/spend",
            new { Resource = resource, Feet = feet });

    [Fact]
    public async Task Owner_of_the_current_turn_character_can_spend_an_action()
    {
        var started = await StartStandardEncounterAsync();

        var response = await SpendAsync(_playerAToken, started.Id, "Action");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SpendResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(_fastCharacterId, body!.CharacterId);
        Assert.False(body.TurnState.ActionAvailable);
        Assert.True(body.TurnState.BonusActionAvailable);
        Assert.True(body.TurnState.ReactionAvailable);
        Assert.Equal(DefaultMovementFeet, body.TurnState.MovementRemainingFeet);

        // state ต้องคงอยู่ข้าม request (อ่านกลับผ่าน GET active ได้)
        var active = await GetActiveAsync(_playerAToken);
        Assert.False(active!.TurnState!.ActionAvailable);
    }

    [Fact]
    public async Task Dm_can_spend_on_behalf_of_the_current_turn_character()
    {
        var started = await StartStandardEncounterAsync();

        var response = await SpendAsync(_dmToken, started.Id, "BonusAction");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SpendResponse>(JsonOptions);
        Assert.False(body!.TurnState.BonusActionAvailable);
        Assert.True(body.TurnState.ActionAvailable);
    }

    /// <summary>
    /// หัวใจของ authorization ใหม่รูปแบบ "DM หรือเจ้าของตัวละครที่ถึงตา" — เจ้าของตัวละคร**อื่น**
    /// ในโต๊ะเดียวกัน (สมาชิกจริง เห็น encounter ได้จริง) ต้องโดน 403 ไม่ใช่ผ่าน
    /// </summary>
    [Fact]
    public async Task Member_who_owns_a_character_whose_turn_it_is_not_gets_403()
    {
        var started = await StartStandardEncounterAsync();

        // เทิร์นแรกเป็นของ _fastCharacterId (เจ้าของ = player A) — player B ต้องสั่งไม่ได้
        var response = await SpendAsync(_playerBToken, started.Id, "Action");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_spending_gets_404_not_403()
    {
        var started = await StartStandardEncounterAsync();

        var response = await SpendAsync(_outsiderToken, started.Id, "Action");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// double-spend คือ `InvalidOperationException` จาก `ActionEconomyResolver` — ต้องถูกดักแปลงเป็น
    /// **400** ที่ชั้น endpoint ห้ามหลุดเป็น 500 (ถ้าหลุด แปลว่า transaction ของทั้ง request rollback
    /// ไปด้วยตามที่ middleware ใน Program.cs ทำ)
    /// </summary>
    [Fact]
    public async Task Spending_the_same_action_twice_returns_400_not_500()
    {
        var started = await StartStandardEncounterAsync();

        Assert.Equal(HttpStatusCode.OK, (await SpendAsync(_playerAToken, started.Id, "Action")).StatusCode);

        var second = await SpendAsync(_playerAToken, started.Id, "Action");
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Spending_movement_reduces_the_remainder_and_overspending_returns_400()
    {
        var started = await StartStandardEncounterAsync();

        var first = await SpendAsync(_playerAToken, started.Id, "Movement", feet: 10);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<SpendResponse>(JsonOptions);
        Assert.Equal(DefaultMovementFeet - 10, firstBody!.TurnState.MovementRemainingFeet);
        Assert.Equal(DefaultMovementFeet, firstBody.TurnState.MaxMovementFeet);

        var second = await SpendAsync(_playerAToken, started.Id, "Movement", feet: 5);
        var secondBody = await second.Content.ReadFromJsonAsync<SpendResponse>(JsonOptions);
        Assert.Equal(DefaultMovementFeet - 15, secondBody!.TurnState.MovementRemainingFeet);

        var overspend = await SpendAsync(_playerAToken, started.Id, "Movement", feet: DefaultMovementFeet);
        Assert.Equal(HttpStatusCode.BadRequest, overspend.StatusCode);

        // ปฏิเสธแล้วต้องไม่หักทิ้งบางส่วน — คงเหลือเท่าเดิมเป๊ะ
        var active = await GetActiveAsync(_playerAToken);
        Assert.Equal(DefaultMovementFeet - 15, active!.TurnState!.MovementRemainingFeet);
    }

    [Fact]
    public async Task Spending_reaction_and_bonus_action_are_tracked_independently()
    {
        var started = await StartStandardEncounterAsync();

        Assert.Equal(HttpStatusCode.OK, (await SpendAsync(_playerAToken, started.Id, "Reaction")).StatusCode);

        var active = await GetActiveAsync(_playerAToken);
        Assert.False(active!.TurnState!.ReactionAvailable);
        Assert.True(active.TurnState.ActionAvailable);
        Assert.True(active.TurnState.BonusActionAvailable);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SpendAsync(_playerAToken, started.Id, "Reaction")).StatusCode);
    }

    [Fact]
    public async Task Spending_an_unknown_resource_returns_400()
    {
        var started = await StartStandardEncounterAsync();

        var response = await SpendAsync(_playerAToken, started.Id, "LegendaryAction");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Spending_movement_without_feet_returns_400()
    {
        var started = await StartStandardEncounterAsync();

        var response = await SpendAsync(_playerAToken, started.Id, "Movement");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Spending_negative_movement_returns_400()
    {
        var started = await StartStandardEncounterAsync();

        var response = await SpendAsync(_playerAToken, started.Id, "Movement", feet: -5);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Spending_on_an_ended_encounter_returns_409()
    {
        var started = await StartStandardEncounterAsync();
        await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/end", null);

        var response = await SpendAsync(_playerAToken, started.Id, "Action");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>หลัง next-turn สิทธิ์ต้องย้ายตามเทิร์น — คนที่เพิ่งหมดเทิร์นสั่งต่อไม่ได้แล้ว</summary>
    [Fact]
    public async Task Spend_permission_follows_the_turn_pointer()
    {
        var started = await StartStandardEncounterAsync();
        await AuthedClient(_dmToken).PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);

        // ตอนนี้เป็นเทิร์นของ _midCharacterId (เจ้าของ = player B)
        Assert.Equal(HttpStatusCode.Forbidden, (await SpendAsync(_playerAToken, started.Id, "Action")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SpendAsync(_playerBToken, started.Id, "Action")).StatusCode);
    }

    // ── GET active ──────────────────────────────────────────────────────────

    private async Task<EncounterResponse?> GetActiveAsync(string token, Guid? sessionId = null)
    {
        var response = await AuthedClient(token).GetAsync(ActiveUrl(sessionId));
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<EncounterResponse>(JsonOptions);
    }

    [Fact]
    public async Task Any_campaign_member_can_read_the_active_encounter()
    {
        var started = await StartStandardEncounterAsync();

        var active = await GetActiveAsync(_playerBToken);

        Assert.NotNull(active);
        Assert.Equal(started.Id, active!.Id);
        Assert.Equal(3, active.Participants.Count);
        Assert.Equal(new[] { 0, 1, 2 }, active.Participants.Select(p => p.TurnOrder).ToArray());
        Assert.Equal(_fastCharacterId, active.CurrentCharacterId);
        AssertFullTurnState(active.TurnState);
    }

    [Fact]
    public async Task Active_returns_204_when_the_session_has_no_encounter()
    {
        var response = await AuthedClient(_dmToken).GetAsync(ActiveUrl(_emptySessionId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_reading_the_active_encounter_gets_404()
    {
        await StartStandardEncounterAsync();

        var response = await AuthedClient(_outsiderToken).GetAsync(ActiveUrl());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Redis หาย = รีเซ็ตเทิร์นปัจจุบัน (fail closed เป็นทรัพยากรเต็ม ไม่ใช่ 500) ──

    /// <summary>
    /// จำลอง "Redis ล่ม" ด้วยการสลับ <c>IConnectionMultiplexer</c> ใน DI เป็นตัวที่ชี้ไปพอร์ตที่ไม่มี
    /// อะไรฟังอยู่ (`abortConnect=false` เพื่อให้ต่อไม่ติดแล้วโยนตอนใช้งาน ไม่ใช่ตอน construct) —
    /// จงใจ**ไม่**ใช้ fake ของ store เอง เพราะจะกลายเป็นการเทสต์ fake แทนที่จะเป็น catch path จริง
    ///
    /// พฤติกรรมที่ล็อกไว้: ใช้ action ไปแล้วจริง ๆ (บันทึกลง Redis ตัวจริงสำเร็จ) แต่พออ่านตอน Redis
    /// ล่ม ต้องได้ "เทิร์นใหม่เต็มทรัพยากร" — เสียแค่ความสะดวก ไม่ใช่ 500 และไม่ใช่ข้อมูลเกมหาย
    /// (HP/dice roll/participant ยังอยู่ใน Postgres ครบ — เช็คด้วย Participants/CurrentCharacterId)
    /// </summary>
    [Fact]
    public async Task Active_encounter_falls_back_to_full_turn_resources_when_redis_is_unavailable()
    {
        var started = await StartStandardEncounterAsync();
        Assert.Equal(HttpStatusCode.OK, (await SpendAsync(_playerAToken, started.Id, "Action")).StatusCode);

        // ยืนยันก่อนว่า Redis ตัวจริงเก็บ state ไว้จริง (ไม่งั้นเทสต์นี้จะผ่านแบบไร้ความหมาย)
        var beforeOutage = await GetActiveAsync(_playerAToken);
        Assert.False(beforeOutage!.TurnState!.ActionAvailable);

        using var deadRedisFactory = CreateDeadRedisFactory();

        var client = Authed(deadRedisFactory, _playerAToken);
        var response = await client.GetAsync(ActiveUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EncounterResponse>(JsonOptions);
        Assert.NotNull(body);

        // ข้อมูลที่อยู่ใน Postgres ต้องครบเหมือนเดิม
        Assert.Equal(started.Id, body!.Id);
        Assert.Equal(3, body.Participants.Count);
        Assert.Equal(_fastCharacterId, body.CurrentCharacterId);

        // ส่วนที่อยู่ใน Redis อย่างเดียว → รีเซ็ตเป็นเทิร์นใหม่เต็ม ๆ
        AssertFullTurnState(body.TurnState);
    }

    // ── รอบรีวิว (2026-09-03): บั๊ก state รั่วข้ามเทิร์น + race ตอน spend ────────

    /// <summary>
    /// **บั๊กจริงที่ code review จับได้** — `next-turn` เขียน state ใหม่ลง Redis ไม่สำเร็จแล้วแค่ log
    /// warning ต่อ แต่ `CurrentParticipantId` ใน Postgres **เลื่อนไปแล้วและ commit ไปแล้ว** ⇒ key เดิม
    /// ของเทิร์นก่อนหน้ายังค้างอยู่ใน Redis ⇒ ผู้เล่นคนใหม่ที่เพิ่งขึ้นเทิร์นจะอ่านเจอ state ของ
    /// **คนก่อนหน้า** (action ที่ถูกใช้ไปแล้ว) — ไม่ใช่แค่ fail open เฉย ๆ แต่เป็น state รั่วข้ามเทิร์น
    ///
    /// ⚠️ ทำไม "ตอบ 503 ตอน TrySetAsync ล้ม" **แก้ไม่ได้**: `Results.StatusCode(503)` ไม่ใช่ exception
    /// → transaction ของ middleware ยัง **commit** ตามปกติ → ตัวชี้เทิร์นก็ยังเลื่อนอยู่ดี
    /// และ "ลบ key ก่อนเขียน" ก็แก้ได้แค่กรณี Redis ยังอยู่แต่เขียนไม่ลง — เคสที่เทสต์นี้จำลอง
    /// (Redis ล่มทั้งตัวตอน next-turn) การลบก็ล้มเหมือนกัน
    /// ⇒ ทางแก้ที่ปิดได้ทุกกรณีคือทำให้ **key ผูกกับเทิร์น** (ไม่ใช่ผูกกับ encounter เฉย ๆ)
    /// ค่าของเทิร์นเก่าจึงไม่มีทางถูกอ่านโดยเทิร์นใหม่ได้เลยตั้งแต่ต้น
    /// </summary>
    [Fact]
    public async Task Next_turn_never_leaks_the_previous_participants_state_even_if_redis_write_fails()
    {
        var started = await StartStandardEncounterAsync();

        // เทิร์นของ _fastCharacterId — ใช้ action ไปจริง (เขียนลง Redis ตัวจริงสำเร็จ)
        Assert.Equal(HttpStatusCode.OK, (await SpendAsync(_playerAToken, started.Id, "Action")).StatusCode);

        // next-turn ตอน Redis ใช้ไม่ได้ → Postgres เลื่อนตัวชี้เทิร์นสำเร็จ แต่ Redis เขียนไม่ลง
        using (var deadRedisFactory = CreateDeadRedisFactory())
        {
            var response = await Authed(deadRedisFactory, _dmToken)
                .PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // อ่านด้วย Redis ตัวจริง (ที่ยังมี key เก่าค้างอยู่) — เทิร์นของ _midCharacterId ต้องเต็ม
        var active = await GetActiveAsync(_playerBToken);
        Assert.NotNull(active);
        Assert.Equal(_midCharacterId, active!.CurrentCharacterId);
        AssertFullTurnState(active.TurnState);
    }

    /// <summary>
    /// **ช่องโหว่ business-logic ที่ security review จับได้ (Medium)** — endpoint spend อ่าน state จาก
    /// Redis แล้วเขียนกลับเป็นคนละ round-trip โดยไม่มี WATCH/Lua/CAS ⇒ ยิงสอง request พร้อมกัน
    /// (เช่น Action + Movement) จากผู้เล่นที่ถึงตาเดินคนเดียวกัน ทั้งคู่จะอ่าน state ตั้งต้นเดียวกัน
    /// แล้วเขียนทับกัน — ตัวหลังลบผลของตัวแรกทิ้ง ⇒ ได้ action ฟรีเพิ่มในเทิร์นเดียวกัน
    /// (HTTP ตอบ 200 ว่าใช้ Action แล้ว แต่ state ที่ค้างจริงบอกว่ายังไม่ได้ใช้)
    ///
    /// **สิ่งที่เทสต์นี้ยืนยันคือ invariant ไม่ใช่จำนวน 200** — ทุก operation ที่ระบบ**ตอบ 200**
    /// ต้องปรากฏใน state สุดท้ายจริง · ถ้าระบบเลือกตอบ 409 ให้ตัวที่ชนกัน ถือว่าถูกต้องเช่นกัน
    /// (จึงไม่ flaky: ไม่ได้บังคับว่าต้องสำเร็จทั้งคู่ แค่ห้าม "ตอบสำเร็จแล้วไม่บันทึก")
    ///
    /// วนหลายรอบเพราะ race ไม่ได้เกิดทุกครั้ง — ใช้ next-turn รีเซ็ตเทิร์นระหว่างรอบ และใช้โทเคน DM
    /// ซึ่งสั่งแทนตัวละครที่ถึงตาได้เสมอ ไม่ต้องสลับโทเคนตามผู้เล่น
    /// </summary>
    [Fact]
    public async Task Concurrent_spends_in_the_same_turn_are_never_lost()
    {
        var started = await StartStandardEncounterAsync();
        var client = AuthedClient(_dmToken);

        for (var round = 0; round < 5; round++)
        {
            var spendAction = SpendAsync(_dmToken, started.Id, "Action");
            var spendMovement = SpendAsync(_dmToken, started.Id, "Movement", feet: 10);
            var responses = await Task.WhenAll(spendAction, spendMovement);

            var actionAccepted = responses[0].StatusCode == HttpStatusCode.OK;
            var movementAccepted = responses[1].StatusCode == HttpStatusCode.OK;

            foreach (var response in responses)
            {
                // ยอมรับได้แค่ 200 (บันทึกแล้ว) หรือ 409 (ปฏิเสธเพราะชนกัน ให้ลองใหม่) เท่านั้น
                Assert.True(
                    response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    $"รอบ {round}: ได้ status ที่ไม่ควรเกิด {(int)response.StatusCode}");
            }

            Assert.True(actionAccepted || movementAccepted, $"รอบ {round}: ถูกปฏิเสธทั้งคู่");

            var active = await GetActiveAsync(_dmToken);
            Assert.NotNull(active);

            if (actionAccepted)
            {
                Assert.False(
                    active!.TurnState!.ActionAvailable,
                    $"รอบ {round}: ตอบ 200 ว่าใช้ Action แล้ว แต่ state สุดท้ายบอกว่ายังใช้ได้อยู่ (ผลถูกเขียนทับ)");
            }

            if (movementAccepted)
            {
                Assert.Equal(DefaultMovementFeet - 10, active!.TurnState!.MovementRemainingFeet);
            }

            var next = await client.PostAsync($"{EncounterUrl(started.Id)}/next-turn", null);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }
    }

    // ── ช่องที่ code review ชี้ว่ายังไม่มีเทสต์คุม (2026-09-03) ────────────────

    [Fact]
    public async Task Start_encounter_with_more_than_twenty_characters_returns_400()
    {
        var tooMany = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            EncountersUrl(), new { CharacterIds = tooMany });

        // ต้องถูกปฏิเสธที่ validation ก่อนแตะ DB — ไม่ใช่ 404 จากการไล่หา character ที่ไม่มีจริง
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_encounter_with_an_empty_guid_in_the_list_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(EncountersUrl(), new
        {
            CharacterIds = new[] { _fastCharacterId, Guid.Empty },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Spending_when_no_participant_holds_the_turn_returns_409()
    {
        var started = await StartStandardEncounterAsync();

        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var clear = new NpgsqlCommand(
                "UPDATE encounters SET current_participant_id = NULL WHERE id = @id", conn);
            clear.Parameters.AddWithValue("id", started.Id);
            await clear.ExecuteNonQueryAsync();
        }

        var response = await SpendAsync(_dmToken, started.Id, "Action");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// ตัวละครไร้เจ้าของ (quick play / orphan, `owner_user_id IS NULL`) ที่ถึงตาเดิน — "ไม่มีเจ้าของ"
    /// ต้อง **ไม่** แปลว่า "ใครก็สั่งได้" · สมาชิกธรรมดาต้องโดน 403 เหลือแค่ DM ที่สั่งแทนได้
    /// (`null != Guid` เสมอ จึงไม่มีผู้เล่นคนไหนผ่านเช็คเจ้าของได้เลย)
    /// </summary>
    [Fact]
    public async Task Ownerless_characters_turn_can_only_be_spent_by_the_dm()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            EncountersUrl(_emptySessionId), new { CharacterIds = new[] { _ownerlessCharacterId } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var encounter = await response.Content.ReadFromJsonAsync<EncounterResponse>(JsonOptions);
        Assert.Equal(_ownerlessCharacterId, encounter!.CurrentCharacterId);

        Assert.Equal(HttpStatusCode.Forbidden, (await SpendAsync(_playerAToken, encounter.Id, "Action")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SpendAsync(_dmToken, encounter.Id, "Action")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected_with_401()
    {
        var anonymous = _factory.CreateClient();

        var start = await anonymous.PostAsJsonAsync(
            EncountersUrl(), new { CharacterIds = new[] { _fastCharacterId } });
        Assert.Equal(HttpStatusCode.Unauthorized, start.StatusCode);

        var active = await anonymous.GetAsync(ActiveUrl());
        Assert.Equal(HttpStatusCode.Unauthorized, active.StatusCode);

        var spend = await anonymous.PostAsJsonAsync(
            $"{EncounterUrl(Guid.NewGuid())}/turn-actions/spend", new { Resource = "Action" });
        Assert.Equal(HttpStatusCode.Unauthorized, spend.StatusCode);
    }

    /// <summary>`int.MaxValue` ต้องจบที่ 400 (เกินระยะคงเหลือ) ไม่ใช่ 500 จาก overflow</summary>
    [Fact]
    public async Task Spending_absurdly_large_movement_returns_400_not_500()
    {
        var started = await StartStandardEncounterAsync();

        var response = await SpendAsync(_playerAToken, started.Id, "Movement", feet: int.MaxValue);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── response contracts (นิยามไว้ในเทสต์เอง ไม่พึ่ง internal type ของ Api) ──

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

    private record SpendResponse(
        Guid EncounterId,
        Guid ParticipantId,
        Guid CharacterId,
        string Resource,
        TurnStateResponse TurnState);

    private record EndEncounterResponse(Guid EncounterId, DateTimeOffset EndedAt);
}

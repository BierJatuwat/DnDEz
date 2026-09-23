using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Part F ของแผน "DM AI Assistant" (2026-09-22) — ต่อ encounter ที่ active เข้า system prompt ของ
/// `OrchestratorTurnRunner` ปลดล็อกคำถามกลางไฟต์แบบ "บอสเหลือเลือดเท่าไหร่ ควรทำไง" ให้มีตัวเลขจริงให้คิด
///
/// จุดที่ต้องล็อกด้วยเทสต์ (ไม่ใช่แค่เขียน doc comment):
///  • **2026-09-23** ผู้ช่วย DM (`/assistant`, `AssistantPrompt`) เห็น HP/AC ของทุกตัวในวง — รวม
///    มอนสเตอร์ที่ `NpcStatsVisibleToParty=false` (ช่องนี้ DM-only ไม่มี narration ที่ broadcast เลย
///    จึงไม่ต้อง mask อะไร ต่างจาก `OrchestratorTurnRunner` ด้านล่าง)
///  • เทิร์นของผู้เล่น (`/actions`, `OrchestratorTurnRunner`) **ไม่เห็นตัวเลข** ของมอนสเตอร์ที่ซ่อนไว้ —
///    ป้องกันไม่ให้ narration ที่ broadcast จริงหลุดเลขที่ผู้เล่นควรต้องค้นหาเอง (ดู `CharacterStatsVisibility`)
///  • ไม่มี encounter active — prompt ไม่มีหัวข้อ combat เลย (regression guard ของ Part A/B/D เดิม)
/// </summary>
public class EncounterSystemPromptTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "encounter-prompt-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-encounter-prompt-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _encounterId = Guid.NewGuid();
    private readonly Guid _heroCharacterId = Guid.NewGuid();
    private readonly Guid _hiddenMonsterId = Guid.NewGuid();
    private readonly Guid _visibleMonsterId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fakeAnthropicClient = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;

    public async Task InitializeAsync()
    {
        _fakeAnthropicClient = new FakeAnthropicClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnthropicClient>();
                services.AddSingleton<IAnthropicClient>(_fakeAnthropicClient);
            });
        });

        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Encounter Prompt Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        await InsertCharacterAsync(
            conn, _heroCharacterId, _campaignId, _playerUserId, "Lyra", controlledByDm: false,
            statsVisible: true, hp: 18, maxHp: 24, ac: 15);
        await InsertCharacterAsync(
            conn, _hiddenMonsterId, _campaignId, null, "Goblin Warrior #1", controlledByDm: true,
            statsVisible: false, hp: 3, maxHp: 7, ac: 15);
        await InsertCharacterAsync(
            conn, _visibleMonsterId, _campaignId, null, "Goblin Warrior #2", controlledByDm: true,
            statsVisible: true, hp: 7, maxHp: 7, ac: 15);

        await InsertEncounterAsync(conn, _encounterId, _campaignId, _sessionId, currentParticipantId: null);
        var heroParticipantId = await InsertParticipantAsync(conn, _encounterId, _heroCharacterId, turnOrder: 0);
        await InsertParticipantAsync(conn, _encounterId, _hiddenMonsterId, turnOrder: 1);
        await InsertParticipantAsync(conn, _encounterId, _visibleMonsterId, turnOrder: 2);

        await using (var setTurn = new NpgsqlCommand(
            "UPDATE encounters SET current_participant_id = @p WHERE id = @e", conn))
        {
            setTurn.Parameters.AddWithValue("p", heroParticipantId);
            setTurn.Parameters.AddWithValue("e", _encounterId);
            await setTurn.ExecuteNonQueryAsync();
        }

        var putKeyResponse = await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey });
        putKeyResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Assistant_sees_full_hp_and_ac_of_every_participant_including_hidden_monster()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant",
            new { Content = "สรุปสถานการณ์ตอนนี้หน่อย", SessionId = _sessionId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var system = _fakeAnthropicClient.LastSystemPromptReceived;
        Assert.NotNull(system);

        Assert.Contains("Active combat encounter", system);
        Assert.Contains("Lyra", system);
        Assert.Contains("HP 18/24, AC 15", system);

        // มอนสเตอร์ที่ NpcStatsVisibleToParty=false ก็ยังเห็นเลขเต็ม — ช่องนี้ DM-only ไม่มีทาง broadcast
        Assert.Contains("Goblin Warrior #1", system);
        Assert.Contains("HP 3/7, AC 15", system);
        Assert.Contains("Goblin Warrior #2", system);
        Assert.Contains("HP 7/7, AC 15", system);

        Assert.Contains("Lyra (current turn)", system);
    }

    [Fact]
    public async Task Player_turn_never_sees_exact_numbers_of_a_hidden_monster()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _heroCharacterId, Content = "ฉันมองไปรอบๆสนามรบ" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var system = _fakeAnthropicClient.LastSystemPromptReceived;
        Assert.NotNull(system);

        // เห็นชื่อว่ามีมอนสเตอร์ตัวนี้อยู่ในวง แต่ต้องไม่เห็นตัวเลขจริงของมัน
        Assert.Contains("Goblin Warrior #1", system);
        Assert.DoesNotContain("HP 3/7", system);
        Assert.Contains("stats hidden from the party", system);

        // ตัวที่ DM เปิดให้เห็น (NpcStatsVisibleToParty=true) ยังเห็นเลขได้ตามปกติ
        Assert.Contains("Goblin Warrior #2", system);
        Assert.Contains("HP 7/7, AC 15", system);

        // ตัวละครผู้เล่นเองเห็นเลขได้เสมอไม่ว่าใครเป็นคนเริ่มเทิร์น
        Assert.Contains("HP 18/24, AC 15", system);
    }

    [Fact]
    public async Task No_active_encounter_means_no_combat_section_in_the_prompt_at_all()
    {
        // ปิด encounter ก่อน — ต้องกลับไปเหมือน Part A เดิม ไม่มีหัวข้อ combat โผล่มาเฉยๆ
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var end = new NpgsqlCommand(
            "UPDATE encounters SET ended_at = now() WHERE id = @id", conn);
        end.Parameters.AddWithValue("id", _encounterId);
        await end.ExecuteNonQueryAsync();

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant",
            new { Content = "คุยเฉยๆ ไม่มีไฟต์", SessionId = _sessionId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var system = _fakeAnthropicClient.LastSystemPromptReceived;
        Assert.NotNull(system);
        Assert.DoesNotContain("Active combat encounter", system);
        Assert.DoesNotContain("Goblin Warrior", system);
    }

    // ── helpers ──────────────────────────────────────────────────────────

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

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId) =>
        await ExecAsync(conn,
            "INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at) " +
            "VALUES (@id, @c, 'Online', 'Active', NULL, now(), NULL)",
            ("id", id), ("c", campaignId));

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, Guid? ownerUserId, string name, bool controlledByDm,
        bool statsVisible, int hp, int maxHp, int ac) =>
        await ExecAsync(conn,
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @c, @owner, @name, '{}'::jsonb, @hp, @maxHp, 0, @ac, 0, 30,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 3, @dm, @visible, false, false, now())
            """,
            ("id", id), ("c", campaignId), ("owner", (object?)ownerUserId ?? DBNull.Value), ("name", name),
            ("hp", hp), ("maxHp", maxHp), ("ac", ac), ("dm", controlledByDm), ("visible", statsVisible));

    private static async Task InsertEncounterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, Guid sessionId, Guid? currentParticipantId) =>
        await ExecAsync(conn,
            """
            INSERT INTO encounters (id, campaign_id, session_id, current_participant_id, started_at, ended_at)
            VALUES (@id, @c, @s, @cur, now(), NULL)
            """,
            ("id", id), ("c", campaignId), ("s", sessionId), ("cur", (object?)currentParticipantId ?? DBNull.Value));

    private static async Task<Guid> InsertParticipantAsync(
        NpgsqlConnection conn, Guid encounterId, Guid characterId, int turnOrder)
    {
        var id = Guid.NewGuid();
        await ExecAsync(conn,
            """
            INSERT INTO encounter_participants
                (id, encounter_id, character_id, initiative_roll, initiative_modifier, initiative_total, turn_order)
            VALUES (@id, @e, @c, 10, 0, 10, @turnOrder)
            """,
            ("id", id), ("e", encounterId), ("c", characterId), ("turnOrder", turnOrder));
        return id;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"encounter-prompt-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Prompt {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = @id", ("id", _campaignId));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId }));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);
}

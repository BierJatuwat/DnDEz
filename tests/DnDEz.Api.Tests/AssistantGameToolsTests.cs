using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// ผู้ช่วย DM ช่องเดียว — Phase 2: tool คุมเกม (เตรียมมอนสเตอร์, ต่อสู้, แผนที่)
///
/// tool ทุกตัวเรียก operation เดียวกับ endpoint ที่ DM กดเอง (เทสต์ของ endpoint เดิมคุม logic ข้างในอยู่แล้ว) —
/// ไฟล์นี้จึงเน้นสิ่งที่เป็นของผู้ช่วยเอง: ทำงานต่อกันได้ในคำสั่งเดียว, การขออนุญาตตาม dial ของแต่ละหมวด,
/// การ์ดอนุมัติโชว์ชื่อไม่ใช่ id, และ id ที่ Claude ส่งมาข้ามแคมเปญ/เซสชันถูกปฏิเสธ
/// </summary>
public class AssistantGameToolsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "assistant-game-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-game-tools-tests-0";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _noSceneSessionId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _goblinTemplateId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherTemplateId = Guid.NewGuid();
    private readonly Guid _otherCharacterId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();
    private readonly Guid _otherSceneId = Guid.NewGuid();
    private readonly Guid _otherTokenId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fake = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;

    public async Task InitializeAsync()
    {
        _fake = new FakeAnthropicClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnthropicClient>();
                services.AddSingleton<IAnthropicClient>(_fake);
            }));

        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Game tools {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _noSceneSessionId, _campaignId);
        await InsertSceneAsync(conn, _sceneId, _sessionId, "[]");
        await InsertCharacterAsync(conn, _heroId, _campaignId, _playerUserId, "Hero", controlledByDm: false);
        await InsertMonsterAsync(conn, _goblinTemplateId, _campaignId, "Goblin Warrior", ac: 15, hp: 7);

        // แคมเปญอื่นของ DM คนเดียวกัน — id ของที่นี่ต้องใช้ข้ามมาไม่ได้แม้ DM จะเป็นเจ้าของทั้งคู่
        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"Game tools other {_suffix}");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");
        await InsertMonsterAsync(conn, _otherTemplateId, _otherCampaignId, "Other Ogre", ac: 11, hp: 59);
        await InsertCharacterAsync(conn, _otherCharacterId, _otherCampaignId, null, "Other NPC", controlledByDm: true);
        await InsertSessionAsync(conn, _otherSessionId, _otherCampaignId);
        await InsertSceneAsync(conn, _otherSceneId, _otherSessionId,
            JsonSerializer.Serialize(new[] { new { id = _otherTokenId, entityType = "Character", entityId = _otherCharacterId, x = 1.0, y = 1.0 } }));

        (await AuthedClient().PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey })).EnsureSuccessStatusCode();
    }

    // ── ทำงานต่อกันในคำสั่งเดียว ──────────────────────────────────────────

    [Fact]
    public async Task One_request_spawns_monsters_then_starts_a_fight_with_the_ids_it_just_got()
    {
        await SetDialAsync("RulesCombat", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("spawn_monsters", new { monster_id = _goblinTemplateId.ToString(), count = 2 })));
        _fake.AgentScript.Enqueue(ScriptedAgentTurn.From(messages =>
        {
            var spawnResult = LastToolResultText(messages);
            var ids = Regex.Matches(spawnResult, @"id=([0-9a-f\-]{36})").Select(m => m.Groups[1].Value).ToList();
            return new ScriptedAgentTurn("",
                ("start_encounter", new { character_ids = ids.Prepend(_heroId.ToString()).ToArray() }));
        }));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("เปิดไฟต์แล้วครับ"));

        var body = await AskAsync("เอาก็อบลิน 2 ตัวลงแล้วเปิดไฟต์กับปาร์ตี้", _sessionId);

        Assert.Equal("completed", body.Status);
        Assert.Equal(["spawn_monsters", "start_encounter"], body.Activity.Select(a => a.Tool));
        Assert.All(body.Activity, a => Assert.Equal("done", a.Status));
        Assert.Equal(3L, await ScalarAsync(
            """
            SELECT count(*) FROM encounter_participants p JOIN encounters e ON e.id = p.encounter_id
            WHERE e.session_id = @s AND e.ended_at IS NULL
            """, ("s", _sessionId)));
        Assert.Contains("Goblin Warrior #1", body.Activity[1].Summary);
    }

    [Fact]
    public async Task Next_turn_and_end_encounter_act_on_the_sessions_running_fight()
    {
        await SetDialAsync("RulesCombat", "Autonomous");
        var spawned = await SpawnViaApiAsync(1);
        (await AuthedClient().PostAsJsonAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/encounters",
            new { CharacterIds = new[] { _heroId, spawned[0] } })).EnsureSuccessStatusCode();

        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("next_turn", new { })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("end_encounter", new { })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("จบไฟต์แล้ว"));

        var body = await AskAsync("เลื่อนเทิร์นแล้วจบไฟต์", _sessionId);

        Assert.All(body.Activity, a => Assert.Equal("done", a.Status));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM encounters WHERE session_id = @s AND ended_at IS NULL", ("s", _sessionId)));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM characters WHERE id = @id", ("id", spawned[0])));
        Assert.Equal(1L, await ScalarAsync(
            "SELECT count(*) FROM campaign_journal_entries WHERE campaign_id = @c AND authored_by = 'System'", ("c", _campaignId)));
    }

    [Fact]
    public async Task Add_from_srd_then_update_its_stats()
    {
        await SetDialAsync("RulesCombat", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("search_srd_monsters", new { query = "goblin" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("add_monster_from_srd", new { key = "goblin-boss" })));
        _fake.AgentScript.Enqueue(ScriptedAgentTurn.From(messages =>
        {
            var id = Regex.Match(LastToolResultText(messages), @"id=([0-9a-f\-]{36})").Groups[1].Value;
            return new ScriptedAgentTurn("", ("update_monster_stats", new { monster_id = id, hit_points = 40 }));
        }));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("เรียบร้อย"));

        var body = await AskAsync("เพิ่ม goblin boss แล้วให้ HP 40", sessionId: null);

        Assert.All(body.Activity, a => Assert.Equal("done", a.Status));
        Assert.Contains("goblin-boss", ToolResultText(_fake.AgentCalls[1]));
        Assert.Equal(40L, await ScalarAsync(
            "SELECT hit_points::bigint FROM monster_stat_blocks WHERE campaign_id = @c AND srd_key = 'goblin-boss'", ("c", _campaignId)));
    }

    // ── แผนที่ ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Map_tools_place_move_and_remove_tokens_on_the_active_scene()
    {
        await SetDialAsync("MapScene", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("place_token", new { entity_id = _heroId.ToString(), x = 2, y = 3 }),
            ("place_token", new { entity_id = _goblinTemplateId.ToString(), x = 5, y = 5 })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("วางแล้ว"));
        var placed = await AskAsync("วาง Hero กับก็อบลินลงแผนที่", _sessionId);

        Assert.All(placed.Activity, a => Assert.Equal("done", a.Status));
        var tokens = await SceneTokensAsync();
        Assert.Contains(tokens, t => t.EntityId == _heroId && t.EntityType == "Character" && t is { X: 2, Y: 3 });
        Assert.Contains(tokens, t => t.EntityId == _goblinTemplateId && t.EntityType == "Monster");

        var heroToken = tokens.Single(t => t.EntityId == _heroId).Id;
        var goblinToken = tokens.Single(t => t.EntityId == _goblinTemplateId).Id;
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("move_token", new { token_id = heroToken.ToString(), x = 7, y = 8 }),
            ("remove_token", new { token_id = goblinToken.ToString() })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ย้ายแล้ว"));
        var moved = await AskAsync("ย้าย Hero แล้วเอาก็อบลินออก", _sessionId);

        Assert.Contains("Hero", moved.Activity[0].Summary);
        var after = await SceneTokensAsync();
        Assert.Contains(after, t => t.Id == heroToken && t is { X: 7, Y: 8 });
        Assert.DoesNotContain(after, t => t.Id == goblinToken);
    }

    [Fact]
    public async Task Fog_can_be_revealed_and_covered_again()
    {
        await SetDialAsync("MapScene", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("reveal_fog", new { x = 4, y = 4, radius = 3 })));
        _fake.AgentScript.Enqueue(ScriptedAgentTurn.From(messages =>
        {
            var areaId = Regex.Match(LastToolResultText(messages), @"id=([0-9a-f\-]{36})").Groups[1].Value;
            return new ScriptedAgentTurn("", ("hide_fog", new { area_id = areaId }));
        }));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ปิดกลับแล้ว"));

        var body = await AskAsync("เปิดหมอกแล้วปิดกลับ", _sessionId);

        Assert.All(body.Activity, a => Assert.Equal("done", a.Status));
        var fog = await ScalarTextAsync("SELECT fog_layer::text FROM scenes WHERE id = @id", ("id", _sceneId));
        Assert.DoesNotContain("\"radius\":3", fog);
    }

    [Fact]
    public async Task Map_tools_report_a_clear_error_when_the_session_has_no_active_scene()
    {
        await SetDialAsync("MapScene", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("place_token", new { entity_id = _heroId.ToString(), x = 1, y = 1 })));

        var body = await AskAsync("วาง Hero", _noSceneSessionId);

        Assert.Equal("error", body.Activity.Single().Status);
        Assert.Contains("no active map scene", ToolResultText(_fake.AgentCalls[1]));
    }

    // ── การขออนุญาตตามหมวด + การ์ดโชว์ชื่อ ──────────────────────────────────

    [Fact]
    public async Task Each_category_follows_its_own_dial_and_cards_show_names_not_ids()
    {
        await SetDialAsync("RulesCombat", "Suggest");
        await SetDialAsync("MapScene", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("place_token", new { entity_id = _heroId.ToString(), x = 1, y = 1 }),
            ("spawn_monsters", new { monster_id = _goblinTemplateId.ToString(), count = 3 })));

        var paused = await AskAsync("วาง Hero แล้วเอาก็อบลิน 3 ตัวลง", _sessionId);

        // แผนที่ (Autonomous) ทำไปแล้ว · ต่อสู้ (Suggest) รออนุมัติ
        Assert.Equal("done", paused.Activity.Single(a => a.Tool == "place_token").Status);
        var card = paused.Pending.Single();
        Assert.Equal("spawn_monsters", card.Tool);
        Assert.Contains("Goblin Warrior ×3", card.Description);
        Assert.DoesNotContain(_goblinTemplateId.ToString(), card.Description);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM characters WHERE spawned_from_monster_id = @m", ("m", _goblinTemplateId)));

        await DecideAsync(paused.RunId, card.ToolUseId, approve: true);
        Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM characters WHERE spawned_from_monster_id = @m", ("m", _goblinTemplateId)));
    }

    [Fact]
    public async Task Manual_dial_hides_that_categorys_tools_only()
    {
        await SetDialAsync("MapScene", "Manual");
        await SetDialAsync("RulesCombat", "Autonomous");

        await AskAsync("มีอะไรให้ทำบ้าง", _sessionId);

        var offered = _fake.AgentCalls.Single().ToolNames;
        Assert.DoesNotContain("place_token", offered);
        Assert.DoesNotContain("reveal_fog", offered);
        Assert.Contains("spawn_monsters", offered);
        Assert.Contains("start_encounter", offered);
    }

    [Fact]
    public async Task Session_tools_are_hidden_without_a_session_but_prep_tools_stay()
    {
        await AskAsync("เตรียมเกม", sessionId: null);

        var offered = _fake.AgentCalls.Single().ToolNames;
        Assert.DoesNotContain("start_encounter", offered);
        Assert.DoesNotContain("place_token", offered);
        Assert.Contains("search_srd_monsters", offered);
        Assert.Contains("add_monster_from_srd", offered);
        Assert.Contains("spawn_monsters", offered);
    }

    // ── id ข้ามแคมเปญ/เซสชัน ──────────────────────────────────────────────

    [Fact]
    public async Task Ids_from_another_campaign_or_scene_are_refused_even_for_the_same_dm()
    {
        await SetDialAsync("RulesCombat", "Autonomous");
        await SetDialAsync("MapScene", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("spawn_monsters", new { monster_id = _otherTemplateId.ToString(), count = 1 }),
            ("update_monster_stats", new { monster_id = _otherTemplateId.ToString(), hit_points = 1 }),
            ("start_encounter", new { character_ids = new[] { _heroId.ToString(), _otherCharacterId.ToString() } }),
            ("move_token", new { token_id = _otherTokenId.ToString(), x = 9, y = 9 }),
            ("remove_token", new { token_id = _otherTokenId.ToString() })));

        var body = await AskAsync("ลองของข้ามโต๊ะ", _sessionId);

        Assert.All(body.Activity, a => Assert.Equal("error", a.Status));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM characters WHERE spawned_from_monster_id = @m", ("m", _otherTemplateId)));
        Assert.Equal(59L, await ScalarAsync("SELECT hit_points::bigint FROM monster_stat_blocks WHERE id = @id", ("id", _otherTemplateId)));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM encounters WHERE session_id = @s", ("s", _sessionId)));
        var otherTokens = await ScalarTextAsync("SELECT tokens::text FROM scenes WHERE id = @id", ("id", _otherSceneId));
        Assert.Contains(_otherTokenId.ToString(), otherTokens);
        Assert.Contains("\"x\": 1", otherTokens.Replace("\"x\":1", "\"x\": 1"));
    }

    [Fact]
    public async Task Invalid_stats_are_rejected_with_the_endpoints_own_reason()
    {
        await SetDialAsync("RulesCombat", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("update_monster_stats", new { monster_id = _goblinTemplateId.ToString(), armor_class = 500 })));

        var body = await AskAsync("ให้ AC 500", sessionId: null);

        Assert.Equal("error", body.Activity.Single().Status);
        Assert.Contains("invalid_armor_class", ToolResultText(_fake.AgentCalls[1]));
        Assert.Equal(15L, await ScalarAsync("SELECT armor_class::bigint FROM monster_stat_blocks WHERE id = @id", ("id", _goblinTemplateId)));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string LastToolResultText(IReadOnlyList<AgentMessage> messages) =>
        string.Join("\n", messages.Last(m => m.Role == "user").Content.EnumerateArray()
            .Select(b => b.GetProperty("content").GetString()));

    private static string ToolResultText(AgentCall call) => LastToolResultText(call.Messages);

    private async Task<List<Guid>> SpawnViaApiAsync(int count)
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_goblinTemplateId}/spawn", new { Count = count });
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>(JsonOptions);
        return items!.Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    /// <summary>อ่านผ่าน GET scene ของ DM (รวมตำแหน่งที่ยังค้างใน Redis) ไม่ใช่อ่าน DB ตรง — การย้าย token เขียน
    /// Redis ก่อนแล้วค่อย flush</summary>
    private async Task<List<TokenView>> SceneTokensAsync()
    {
        var scene = await AuthedClient().GetFromJsonAsync<JsonElement>(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene", JsonOptions);
        return scene.GetProperty("tokens").EnumerateArray()
            .Select(t => new TokenView(
                t.GetProperty("id").GetGuid(), t.GetProperty("entityType").GetString()!,
                t.GetProperty("entityId").GetGuid(), t.GetProperty("x").GetDouble(), t.GetProperty("y").GetDouble()))
            .ToList();
    }

    private async Task<TurnResponse> AskAsync(string content, Guid? sessionId)
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = content, SessionId = sessionId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TurnResponse>(JsonOptions))!;
    }

    private async Task DecideAsync(Guid runId, string toolUseId, bool approve)
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant/runs/{runId}/decisions", new { ToolUseId = toolUseId, Approve = approve });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SetDialAsync(string subsystem, string level)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn,
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level) VALUES (@id, @c, @s, @l)
            ON CONFLICT (campaign_id, subsystem) DO UPDATE SET level = EXCLUDED.level
            """,
            ("id", Guid.NewGuid()), ("c", _campaignId), ("s", subsystem), ("l", level));
    }

    private static async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarTextAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid owner, string name) =>
        ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @owner, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", id), ("owner", owner), ("name", name));

    private static Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role) =>
        ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @c, @u, @role, now())",
            ("id", Guid.NewGuid()), ("c", campaignId), ("u", userId), ("role", role));

    private static Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId) =>
        ExecAsync(conn,
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @c, 'Online', 'Active', NULL, now(), NULL)
            """,
            ("id", id), ("c", campaignId));

    private static Task InsertSceneAsync(NpgsqlConnection conn, Guid id, Guid sessionId, string tokensJson) =>
        ExecAsync(conn,
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@id, @s, 'Test Scene', '{}', @tokens::jsonb, '{}', true)
            """,
            ("id", id), ("s", sessionId), ("tokens", tokensJson));

    private static Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, Guid? ownerUserId, string name, bool controlledByDm) =>
        ExecAsync(conn,
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @c, @owner, @name, '{}'::jsonb, 20, 20, 0, 12, 0, 30,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @dm, true, false, false, now())
            """,
            ("id", id), ("c", campaignId), ("owner", (object?)ownerUserId ?? DBNull.Value),
            ("name", name), ("dm", controlledByDm));

    private static Task InsertMonsterAsync(NpgsqlConnection conn, Guid id, Guid campaignId, string name, int ac, int hp) =>
        ExecAsync(conn,
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status,
                 speed_feet, reach_feet)
            VALUES (@id, @c, @name, @ac, @hp, '[]'::jsonb, '[]'::jsonb, 'Approved', 30, 5)
            """,
            ("id", id), ("c", campaignId), ("name", name), ("ac", ac), ("hp", hp));

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
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"assistant-game-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Game {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _dmToken);
        return client;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)", ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", new[] { _dmUserId, _playerUserId }));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record TokenView(Guid Id, string EntityType, Guid EntityId, double X, double Y);

    private sealed record Activity(string ToolUseId, string Tool, string Summary, string Status);

    private sealed record PendingCard(string ToolUseId, string Tool, string Description, string? EditableText);

    private sealed record TurnResponse(
        string Status, Guid RunId, Guid? MessageId, string Text, List<Activity> Activity, List<PendingCard> Pending);
}

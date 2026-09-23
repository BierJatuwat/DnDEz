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
/// ผู้ช่วย DM ช่องเดียว — Phase 3: tool ที่ส่งของถึงผู้เล่น
///
/// หัวใจที่ต้องล็อกด้วยเทสต์: **ข้อความที่ AI แต่งแล้วส่งถึงผู้เล่นต้องให้ DM ยืนยันทุกครั้ง แม้ dial เป็น Autonomous**
/// (ผู้ช่วยเห็นความลับของแคมเปญ ทางนี้คือทางเดียวที่ความลับจะไปถึงผู้เล่นได้) และสิ่งที่ถูกส่งจริงคือข้อความที่ DM
/// อนุมัติ/แก้แล้ว ไม่ใช่ร่างของ AI
/// </summary>
public class AssistantTableToolsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "assistant-table-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-table-tools-tests-";
    private const string SecretMarker = "SECRET-MARKER-the-innkeeper-is-a-lich";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _innkeeperId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherNpcId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fake = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;

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
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Table tools {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await ExecAsync(conn,
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @c, 'Online', 'Active', NULL, now(), NULL)
            """,
            ("id", _sessionId), ("c", _campaignId));
        await InsertCharacterAsync(conn, _heroId, _campaignId, _playerUserId, "Hero", controlledByDm: false);
        await InsertCharacterAsync(conn, _innkeeperId, _campaignId, null, "Innkeeper", controlledByDm: true);

        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"Table tools other {_suffix}");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");
        await InsertCharacterAsync(conn, _otherNpcId, _otherCampaignId, null, "Other NPC", controlledByDm: true);

        (await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey })).EnsureSuccessStatusCode();
    }

    // ── กฎเหล็ก: ข้อความถึงผู้เล่นต้องผ่านตา DM เสมอ ──────────────────────────────

    [Fact]
    public async Task Narration_waits_for_the_dm_even_when_the_dial_is_autonomous()
    {
        await SetDialAsync("Narration", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("narrate_to_players", new { text = "ร่างของ AI" })));

        var paused = await AskAsync("บรรยายฉากเปิด");

        Assert.Equal("awaiting_approval", paused.Status);
        Assert.Equal("ร่างของ AI", paused.Pending.Single().EditableText);
        Assert.Equal(0L, await CountPublicMessagesAsync());
    }

    [Fact]
    public async Task What_gets_sent_is_the_dms_edited_text_not_the_ai_draft()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("narrate_to_players", new { text = $"ร่างที่หลุดความลับ {SecretMarker}" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ส่งแล้ว"));
        var paused = await AskAsync("บรรยายโรงเตี๊ยม");

        var done = await DecideAsync(paused.RunId, paused.Pending.Single().ToolUseId, approve: true,
            editedText: "โรงเตี๊ยมเงียบผิดปกติ มีกลิ่นดินชื้นลอยมาจากห้องใต้ดิน");

        Assert.Equal("completed", done.Status);
        var messages = await PublicMessagesAsync();
        var sent = Assert.Single(messages);
        Assert.Equal("โรงเตี๊ยมเงียบผิดปกติ มีกลิ่นดินชื้นลอยมาจากห้องใต้ดิน", sent.Content);
        Assert.Null(sent.CharacterId);
        Assert.DoesNotContain(messages, m => m.Content.Contains(SecretMarker));
    }

    [Fact]
    public async Task Rejected_narration_is_never_sent()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("narrate_to_players", new { text = "ไม่ควรถึงผู้เล่น" })));
        var paused = await AskAsync("บรรยาย");

        await DecideAsync(paused.RunId, paused.Pending.Single().ToolUseId, approve: false);

        Assert.Equal(0L, await CountPublicMessagesAsync());
    }

    [Fact]
    public async Task Empty_edited_text_is_refused_and_nothing_is_sent()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("narrate_to_players", new { text = "ร่าง" })));
        var paused = await AskAsync("บรรยาย");

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant/runs/{paused.RunId}/decisions",
            new { ToolUseId = paused.Pending.Single().ToolUseId, Approve = true, EditedText = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await CountPublicMessagesAsync());
    }

    [Fact]
    public async Task Approved_narration_becomes_public_history_for_the_next_player_turn()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("narrate_to_players", new { text = "NARRATION-MARKER ประตูเปิดออก" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ส่งแล้ว"));
        var paused = await AskAsync("บรรยาย");
        await DecideAsync(paused.RunId, paused.Pending.Single().ToolUseId, approve: true);

        var action = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _heroId, Content = "ฉันเดินเข้าไป" });

        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        Assert.Contains(_fake.LastHistoryReceived!, h => h.Text.Contains("NARRATION-MARKER"));
    }

    [Fact]
    public async Task Manual_narration_dial_hides_narration_but_not_npc_dialogue()
    {
        await SetDialAsync("Narration", "Manual");

        await AskAsync("มีอะไรให้ทำ");

        var offered = _fake.AgentCalls.Single().ToolNames;
        Assert.DoesNotContain("narrate_to_players", offered);
        Assert.DoesNotContain("generate_session_recap", offered);
        Assert.Contains("npc_say", offered);
    }

    [Fact]
    public async Task Table_tools_need_a_selected_session()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = "เตรียมเกม" });
        response.EnsureSuccessStatusCode();

        var offered = _fake.AgentCalls.Single().ToolNames;
        Assert.DoesNotContain("narrate_to_players", offered);
        Assert.DoesNotContain("npc_say", offered);
        Assert.DoesNotContain("generate_session_recap", offered);
    }

    // ── บทพูด NPC ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Npc_line_is_sent_under_the_npcs_name_after_approval()
    {
        await SetDialAsync("NpcDialogue", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("npc_say", new { character_id = _innkeeperId.ToString(), text = "ห้องเต็มแล้วครับท่าน" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("พูดแล้ว"));

        var paused = await AskAsync("ให้เจ้าของโรงเตี๊ยมพูด");
        Assert.Equal("Innkeeper พูดกับผู้เล่น", paused.Pending.Single().Description);

        await DecideAsync(paused.RunId, paused.Pending.Single().ToolUseId, approve: true);

        var sent = Assert.Single(await PublicMessagesAsync());
        Assert.Equal(_innkeeperId, sent.CharacterId);
        Assert.Equal("ห้องเต็มแล้วครับท่าน", sent.Content);
    }

    [Fact]
    public async Task Ai_cannot_speak_as_a_player_character_or_an_npc_from_another_campaign()
    {
        foreach (var target in new[] { _heroId, _otherNpcId })
        {
            _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
                ("npc_say", new { character_id = target.ToString(), text = "พูดแทน" })));
            _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ok"));
            var paused = await AskAsync("ให้พูด");
            var done = await DecideAsync(paused.RunId, paused.Pending.Single().ToolUseId, approve: true);

            Assert.Equal("error", done.Activity.Single(a => a.Tool == "npc_say").Status);
        }

        Assert.Equal(0L, await CountPublicMessagesAsync());
    }

    // ── recap ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generated_recap_keeps_secrets_out_of_the_player_version()
    {
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await InsertMessageAsync(conn, "Player", dmPrivate: false, "We enter the inn PUBLIC-MARKER", minutesAgo: 2);
            await InsertMessageAsync(conn, "Dm", dmPrivate: true, $"plan {SecretMarker}", minutesAgo: 1);
        }

        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("generate_session_recap", new { })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("สรุปแล้ว"));
        var paused = await AskAsync($"สรุปเซสชันนี้ให้หน่อย {SecretMarker}");
        Assert.Equal("generate_session_recap", paused.Pending.Single().Tool);

        await DecideAsync(paused.RunId, paused.Pending.Single().ToolUseId, approve: true);

        var recap = await AuthedClient(_playerToken).GetFromJsonAsync<JsonElement>(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/recap", JsonOptions);
        var playerText = recap.GetProperty("playerVersionText").GetString()!;
        Assert.Contains("PUBLIC-MARKER", playerText);
        Assert.DoesNotContain(SecretMarker, playerText);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<TurnResponse> AskAsync(string content)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = content, SessionId = _sessionId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TurnResponse>(JsonOptions))!;
    }

    private async Task<TurnResponse> DecideAsync(Guid runId, string toolUseId, bool approve, string? editedText = null)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant/runs/{runId}/decisions",
            new { ToolUseId = toolUseId, Approve = approve, EditedText = editedText });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TurnResponse>(JsonOptions))!;
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

    /// <summary>ข้อความสาธารณะที่ AI เป็นคนเขียนลงเซสชัน (= สิ่งที่ถูกส่งถึงผู้เล่น)</summary>
    private async Task<List<(string Content, Guid? CharacterId)>> PublicMessagesAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT content, character_id FROM messages
            WHERE session_id = @s AND dm_private = false AND author_type = 'Ai'
            ORDER BY created_at
            """, conn);
        cmd.Parameters.AddWithValue("s", _sessionId);
        var rows = new List<(string, Guid?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)));
        }

        return rows;
    }

    private async Task<long> CountPublicMessagesAsync() => (await PublicMessagesAsync()).Count;

    private Task InsertMessageAsync(NpgsqlConnection conn, string authorType, bool dmPrivate, string content, int minutesAgo) =>
        ExecAsync(conn,
            """
            INSERT INTO messages (id, session_id, author_type, content, source, dm_private, created_at)
            VALUES (@id, @s, @author, @content, 'Player', @private, now() - make_interval(mins => @ago))
            """,
            ("id", Guid.NewGuid()), ("s", _sessionId), ("author", authorType), ("content", content),
            ("private", dmPrivate), ("ago", minutesAgo));

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
            Email = $"assistant-table-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Table {label}",
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
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)", ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", new[] { _dmUserId, _playerUserId }));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record Activity(string ToolUseId, string Tool, string Summary, string Status);

    private sealed record PendingCard(string ToolUseId, string Tool, string Description, string? EditableText);

    private sealed record TurnResponse(
        string Status, Guid RunId, Guid? MessageId, string Text, List<Activity> Activity, List<PendingCard> Pending);
}

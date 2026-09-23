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
/// ผู้ช่วย DM ช่องเดียว (2026-09-23) — ช่วงที่ 1: loop หลายขั้น, หยุดรอ DM อนุมัติ/ทำต่อ, commit ทีละขั้น, tool
/// อ่าน + บันทึก + ทอยเต๋า
///
/// contract:
///   POST /campaigns/{c}/assistant { Content, SessionId? } [DM] → 200 { Status, RunId, MessageId?, Text, Activity[], Pending[] }
///   POST /campaigns/{c}/assistant/runs/{r}/decisions { ToolUseId, Approve, EditedText? } [DM] → 200 เหมือนข้างบน · 409 ถ้าไม่ได้รออยู่
///   GET  /campaigns/{c}/assistant [DM] → 200 { Messages[], PendingRun? }
///
/// fake ตอบตามสคริปต์ที่ใส่ใน <see cref="FakeAnthropicClient.AgentScript"/> ทีละรอบ และจดทุก call ไว้ใน
/// <see cref="FakeAnthropicClient.AgentCalls"/> — เทสต์ตรวจทั้งผลใน DB และสิ่งที่ถูกส่งกลับไปหา Claude จริง
/// </summary>
public class AssistantEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "assistant-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-assistant-tests-00";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();
    private readonly Guid _otherJournalEntryId = Guid.NewGuid();
    private readonly Guid _noKeyCampaignId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fake = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;
    private Guid _noKeyDmUserId;
    private string _noKeyDmToken = null!;

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
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");
        (_noKeyDmUserId, _noKeyDmToken) = await RegisterAsync("dm-no-key");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Assistant {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"Assistant other {_suffix}");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");
        await InsertCampaignAsync(conn, _noKeyCampaignId, _noKeyDmUserId, $"Assistant no key {_suffix}");
        await InsertMembershipAsync(conn, _noKeyCampaignId, _noKeyDmUserId, "Dm");
        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _otherSessionId, _otherCampaignId);
        await ExecAsync(conn,
            """
            INSERT INTO campaign_journal_entries (id, campaign_id, session_id, title, content, authored_by, created_at)
            VALUES (@id, @c, NULL, 'Other table note', 'untouched', 'Dm', now())
            """,
            ("id", _otherJournalEntryId), ("c", _otherCampaignId));

        (await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey })).EnsureSuccessStatusCode();
    }

    // ── ถาม-ตอบธรรมดา ─────────────────────────────────────────────────────

    [Fact]
    public async Task Plain_question_completes_and_stores_both_sides_with_the_session()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ก็อบลิน 4 ตัวพอดีสำหรับปาร์ตี้เลเวล 2"));

        var body = await AskAsync("บาลานซ์ไหม", _sessionId);

        Assert.Equal("completed", body.Status);
        Assert.Contains("ก็อบลิน 4 ตัว", body.Text);
        Assert.Empty(body.Activity);

        var history = await GetHistoryAsync();
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal(("Dm", "บาลานซ์ไหม"), (history.Messages[0].Role, history.Messages[0].Content));
        Assert.Equal("Ai", history.Messages[1].Role);
        Assert.All(history.Messages, m => Assert.Equal(_sessionId, m.SessionId));
        Assert.Null(history.PendingRun);
    }

    [Fact]
    public async Task System_prompt_includes_dm_secrets_and_the_untrusted_player_text_rule()
    {
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await InsertStoryBeatAsync(conn, _campaignId, "The Mayor's Bargain", "Active", "Approved",
                dmNotes: "SECRET-NOTE-mayor-is-villain", secretsJson: """[{"text":"SECRET-TWIST-decoy"}]""");
            await InsertStoryBeatAsync(conn, _campaignId, "Unreviewed", "Upcoming", "Pending",
                dmNotes: "SHOULD-NOT-APPEAR");
        }

        await AskAsync("ควรใบ้ยังไง", sessionId: null);

        var system = _fake.AgentCalls.Single().System!;
        Assert.Contains("SECRET-NOTE-mayor-is-villain", system);
        Assert.Contains("SECRET-TWIST-decoy", system);
        Assert.DoesNotContain("SHOULD-NOT-APPEAR", system);
        Assert.DoesNotContain("Unreviewed", system);
        Assert.Contains("untrusted data", system);
    }

    // ── loop หลายขั้น ─────────────────────────────────────────────────────

    [Fact]
    public async Task Multi_step_request_runs_every_tool_and_returns_all_results_to_claude_in_order()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ขอดูก่อน",
            ("get_game_state", new { }),
            ("write_journal_entry", new { title = "แผนตอนหน้า", content = "ปาร์ตี้จะเจอนายกฯ" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("จดให้แล้วครับ"));

        var body = await AskAsync("จดแผนตอนหน้าให้หน่อย", _sessionId);

        Assert.Equal("completed", body.Status);
        Assert.Equal(["get_game_state", "write_journal_entry"], body.Activity.Select(a => a.Tool));
        Assert.All(body.Activity, a => Assert.Equal("done", a.Status));

        // รอบที่สองต้องได้ tool_result ครบทั้งสองตัวตามลำดับเดิม
        var toolResults = LastUserBlocks(_fake.AgentCalls[1]);
        Assert.Equal(2, toolResults.Count);
        Assert.All(toolResults, b => Assert.Equal("tool_result", b.GetProperty("type").GetString()));
        Assert.Contains("Characters:", toolResults[0].GetProperty("content").GetString());
        Assert.Equal("Journal entry saved.", toolResults[1].GetProperty("content").GetString());

        Assert.Equal(("Ai", _sessionId), await ReadJournalAsync("แผนตอนหน้า"));
    }

    [Fact]
    public async Task Steps_are_committed_so_earlier_work_survives_a_later_ai_failure()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("write_journal_entry", new { title = "ก่อนล่ม", content = "ต้องยังอยู่" })));
        _fake.AgentScript.Enqueue(ScriptedAgentTurn.Failure);

        var body = await AskAsync("จดแล้วทำต่อ", sessionId: null);

        Assert.Equal("completed", body.Status);
        Assert.Contains("เรียก AI ไม่สำเร็จ", body.Text);
        Assert.Equal("Ai", (await ReadJournalAsync("ก่อนล่ม")).AuthoredBy);
    }

    [Fact]
    public async Task Checkpointed_steps_survive_an_unhandled_crash_later_in_the_same_request()
    {
        // เทสต์ข้างบนพิสูจน์ไม่ได้ว่ามี checkpoint จริง (runner จับ error ของ Anthropic เองแล้ว commit ตอนจบ
        // request อยู่ดี) — ตัวนี้ให้ exception ลอยถึง middleware ซึ่ง rollback transaction ปัจจุบัน: ถ้าไม่มี
        // checkpoint บันทึกจากขั้นแรกจะถูก rollback ไปด้วย
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("write_journal_entry", new { title = "ก่อนพัง", content = "ต้องยังอยู่" })));
        _fake.AgentScript.Enqueue(ScriptedAgentTurn.Crash);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _dmToken);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var response = await client.PostAsJsonAsync(
                $"/campaigns/{_campaignId}/assistant", new { Content = "จดแล้วพัง" });
            response.EnsureSuccessStatusCode();
        });

        Assert.Equal("Ai", (await ReadJournalAsync("ก่อนพัง")).AuthoredBy);
    }

    [Fact]
    public async Task Rls_context_survives_each_checkpoint()
    {
        // ขั้นแรกเขียนบันทึก (checkpoint = commit + เปิด transaction ใหม่) ขั้นสองค้นบันทึกเดียวกัน — ตาราง
        // นี้ dm_only ถ้า checkpoint ลืมตั้ง app.user_id/app.campaign_id ซ้ำ การค้นจะได้ 0 แถว
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("write_journal_entry", new { title = "CheckpointMarker", content = "x" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("search_campaign_journal", new { query = "CheckpointMarker" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("เจอแล้ว"));

        await AskAsync("จดแล้วค้นดู", sessionId: null);

        var searchResult = LastUserBlocks(_fake.AgentCalls[2]).Single().GetProperty("content").GetString();
        Assert.Contains("CheckpointMarker", searchResult);
    }

    [Fact]
    public async Task Loop_stops_at_the_call_cap()
    {
        for (var i = 0; i < 12; i++)
        {
            _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("get_game_state", new { })));
        }

        var body = await AskAsync("วนไปเรื่อย ๆ", sessionId: null);

        Assert.Equal(8, _fake.AgentCalls.Count);
        Assert.Equal("completed", body.Status);
        Assert.Contains("เพดาน", body.Text);
    }

    // ── การขออนุญาตตาม dial ────────────────────────────────────────────────

    [Fact]
    public async Task Session_tools_are_offered_only_when_a_session_is_selected()
    {
        await AskAsync("ไม่มีเซสชัน", sessionId: null);
        await AskAsync("มีเซสชัน", _sessionId);

        Assert.DoesNotContain("roll_dice", _fake.AgentCalls[0].ToolNames);
        Assert.Contains("roll_dice", _fake.AgentCalls[1].ToolNames);
    }

    [Fact]
    public async Task Manual_dial_neither_offers_nor_executes_the_tool_even_if_claude_calls_it()
    {
        await SetDialAsync("RulesCombat", "Manual");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("roll_dice", new { expression = "1d20" })));

        var body = await AskAsync("ทอยให้หน่อย", _sessionId);

        Assert.DoesNotContain("roll_dice", _fake.AgentCalls[0].ToolNames);
        Assert.Equal("error", body.Activity.Single().Status);
        Assert.Equal(0L, await CountDiceRollsAsync());
    }

    [Fact]
    public async Task Autonomous_dial_runs_the_tool_without_asking()
    {
        await SetDialAsync("RulesCombat", "Autonomous");
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("roll_dice", new { expression = "1d20+5" })));

        var body = await AskAsync("ทอย initiative", _sessionId);

        Assert.Equal("completed", body.Status);
        Assert.Empty(body.Pending);
        Assert.Equal(1L, await CountDiceRollsAsync());
    }

    [Fact]
    public async Task Suggest_dial_pauses_then_approval_runs_the_tool_and_continues()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ขอทอยนะครับ", ("roll_dice", new { expression = "1d20+5" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ได้ 17 ครับ"));

        var paused = await AskAsync("ทอยให้หน่อย", _sessionId);

        Assert.Equal("awaiting_approval", paused.Status);
        var card = paused.Pending.Single();
        Assert.Equal("roll_dice", card.Tool);
        Assert.Contains("1d20+5", card.Description);
        Assert.Equal(0L, await CountDiceRollsAsync());
        Assert.NotNull((await GetHistoryAsync()).PendingRun);

        var done = await DecideAsync(paused.RunId, card.ToolUseId, approve: true);

        Assert.Equal("completed", done.Status);
        Assert.Equal(1L, await CountDiceRollsAsync());
        Assert.Equal("done", done.Activity.Single(a => a.Tool == "roll_dice").Status);
        Assert.Contains("ได้ 17", done.Text);
        Assert.Null((await GetHistoryAsync()).PendingRun);
    }

    [Fact]
    public async Task Rejected_action_is_not_run_and_claude_is_told_so()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("roll_dice", new { expression = "1d20" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("รับทราบ ไม่ทอย"));

        var paused = await AskAsync("ทอยให้หน่อย", _sessionId);
        var done = await DecideAsync(paused.RunId, paused.Pending.Single().ToolUseId, approve: false);

        Assert.Equal(0L, await CountDiceRollsAsync());
        Assert.Equal("rejected", done.Activity.Single().Status);
        var result = LastUserBlocks(_fake.AgentCalls[1]).Single();
        Assert.Contains("rejected", result.GetProperty("content").GetString());
        Assert.True(result.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task Deciding_the_same_card_twice_is_a_conflict()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("roll_dice", new { expression = "1d20" })));

        var paused = await AskAsync("ทอย", _sessionId);
        var toolUseId = paused.Pending.Single().ToolUseId;
        await DecideAsync(paused.RunId, toolUseId, approve: true);

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant/runs/{paused.RunId}/decisions",
            new { ToolUseId = toolUseId, Approve = true });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1L, await CountDiceRollsAsync());
    }

    [Fact]
    public async Task Edited_text_is_refused_for_actions_that_have_no_text_to_edit()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("roll_dice", new { expression = "1d20" })));
        var paused = await AskAsync("ทอย", _sessionId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant/runs/{paused.RunId}/decisions",
            new { ToolUseId = paused.Pending.Single().ToolUseId, Approve = true, EditedText = "8d6" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await CountDiceRollsAsync());
    }

    [Fact]
    public async Task New_message_abandons_the_pending_run_and_keeps_its_history()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("ขอทอย", ("roll_dice", new { expression = "1d20" })));
        var paused = await AskAsync("ทอย", _sessionId);

        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("เปลี่ยนเรื่องได้ครับ"));
        await AskAsync("ไม่เอาแล้ว ถามเรื่องอื่น", _sessionId);

        var history = await GetHistoryAsync();
        Assert.Null(history.PendingRun);
        Assert.Contains(history.Messages, m => m.Content.Contains("ถูกยกเลิก")
            && m.Activity.Any(a => a is { Tool: "roll_dice", Status: "cancelled" }));
        Assert.Equal(0L, await CountDiceRollsAsync());

        var late = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant/runs/{paused.RunId}/decisions",
            new { ToolUseId = paused.Pending.Single().ToolUseId, Approve = true });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Next_request_remembers_what_was_done_before()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("write_journal_entry", new { title = "จำไว้", content = "x" })));
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("จดแล้ว"));
        await AskAsync("จดให้หน่อย", sessionId: null);

        await AskAsync("เมื่อกี้ทำอะไรไป", sessionId: null);

        var messages = _fake.AgentCalls.Last().Messages;
        var assistantText = messages.Single(m => m.Role == "assistant").Content.GetString();
        Assert.Contains("write_journal_entry", assistantText);
        Assert.Equal("user", messages[0].Role);
    }

    // ── IDOR / สิทธิ์ ───────────────────────────────────────────────────────

    [Fact]
    public async Task Journal_update_with_an_id_from_another_campaign_is_refused()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("",
            ("update_journal_entry", new { entry_id = _otherJournalEntryId.ToString(), content = "hijacked" })));

        var body = await AskAsync("แก้บันทึก", sessionId: null);

        Assert.Equal("error", body.Activity.Single().Status);
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT content FROM campaign_journal_entries WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _otherJournalEntryId);
        Assert.Equal("untouched", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Session_from_another_campaign_is_rejected()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = "x", SessionId = _otherSessionId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_fake.AgentCalls);
    }

    [Fact]
    public async Task Players_and_outsiders_cannot_use_any_assistant_endpoint()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("roll_dice", new { expression = "1d20" })));
        var paused = await AskAsync("ทอย", _sessionId);
        var toolUseId = paused.Pending.Single().ToolUseId;

        foreach (var (token, expected) in new[] { (_playerToken, HttpStatusCode.Forbidden), (_outsiderToken, HttpStatusCode.NotFound) })
        {
            var client = AuthedClient(token);
            Assert.Equal(expected, (await client.PostAsJsonAsync($"/campaigns/{_campaignId}/assistant", new { Content = "x" })).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/campaigns/{_campaignId}/assistant")).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync(
                $"/campaigns/{_campaignId}/assistant/runs/{paused.RunId}/decisions",
                new { ToolUseId = toolUseId, Approve = true })).StatusCode);
        }

        Assert.Equal(0L, await CountDiceRollsAsync());
    }

    [Fact]
    public async Task Missing_api_key_returns_400()
    {
        var response = await AuthedClient(_noKeyDmToken).PostAsJsonAsync(
            $"/campaigns/{_noKeyCampaignId}/assistant", new { Content = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── RLS ของ assistant_runs (state มี system prompt ที่รวมความลับ) ─────────────

    [Fact]
    public async Task Assistant_runs_table_has_rls_enabled_and_forced()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'assistant_runs'", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Player_cannot_read_or_write_assistant_runs_at_the_db_layer()
    {
        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("", ("roll_dice", new { expression = "1d20" })));
        await AskAsync("ทอย", _sessionId);

        Assert.Equal(1L, await CountRunsAsAsync(_dmUserId));
        Assert.Equal(0L, await CountRunsAsAsync(_playerUserId));

        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO assistant_runs (id, campaign_id, dm_user_id, status, state, created_at, updated_at)
            VALUES (@id, @c, @u, 'Running', '{}', now(), now())
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("c", _campaignId);
        insert.Parameters.AddWithValue("u", _playerUserId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    // ── recap ฉบับ DM รวมบทสนทนากับผู้ช่วยในเซสชันนั้น ────────────────────────

    [Fact]
    public async Task Dm_recap_includes_the_session_assistant_chat_but_player_recap_does_not()
    {
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn,
                """
                INSERT INTO messages (id, session_id, author_type, content, source, dm_private, created_at)
                VALUES (@id, @s, 'Player', 'We open the door', 'Player', false, now())
                """,
                ("id", Guid.NewGuid()), ("s", _sessionId));
        }

        _fake.AgentScript.Enqueue(new ScriptedAgentTurn("แผนลับรับทราบ"));
        await AskAsync("ASSISTANT-SECRET-PLAN ให้โทรลล์โผล่ตอนข้ามสะพาน", _sessionId);

        var recap = await (await AuthedClient(_dmToken).PostAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/recap/generate", null))
            .Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Contains("ASSISTANT-SECRET-PLAN", recap.GetProperty("dmVersionText").GetString());
        Assert.DoesNotContain("ASSISTANT-SECRET-PLAN", recap.GetProperty("playerVersionText").GetString());
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<TurnResponse> AskAsync(string content, Guid? sessionId)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = content, SessionId = sessionId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TurnResponse>(JsonOptions))!;
    }

    private async Task<TurnResponse> DecideAsync(Guid runId, string toolUseId, bool approve)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant/runs/{runId}/decisions",
            new { ToolUseId = toolUseId, Approve = approve });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TurnResponse>(JsonOptions))!;
    }

    private async Task<HistoryResponse> GetHistoryAsync() =>
        (await AuthedClient(_dmToken).GetFromJsonAsync<HistoryResponse>($"/campaigns/{_campaignId}/assistant", JsonOptions))!;

    /// <summary>content blocks ของข้อความ user ตัวสุดท้ายใน call นั้น (= tool_result ของรอบก่อน)</summary>
    private static List<JsonElement> LastUserBlocks(AgentCall call) =>
        call.Messages.Last(m => m.Role == "user").Content.EnumerateArray().ToList();

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

    private async Task<long> CountDiceRollsAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM dice_rolls WHERE session_id = @s", conn);
        cmd.Parameters.AddWithValue("s", _sessionId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(string AuthoredBy, Guid? SessionId)> ReadJournalAsync(string title)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT authored_by, session_id FROM campaign_journal_entries WHERE campaign_id = @c AND title = @t", conn);
        cmd.Parameters.AddWithValue("c", _campaignId);
        cmd.Parameters.AddWithValue("t", title);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"journal entry \"{title}\" not found");
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private async Task<long> CountRunsAsAsync(Guid userId)
    {
        var (conn, tx) = await OpenAsAsync(userId);
        await using var _ = conn;
        await using var __ = tx;
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM assistant_runs WHERE campaign_id = @c", conn, tx);
        cmd.Parameters.AddWithValue("c", _campaignId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAsAsync(Guid userId)
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
                         "SELECT set_config('app.user_id', @u, true), set_config('app.campaign_id', @c, true)", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId.ToString());
            cmd.Parameters.AddWithValue("c", _campaignId.ToString());
            await cmd.ExecuteScalarAsync();
        }

        return (conn, tx);
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

    private static Task InsertStoryBeatAsync(
        NpgsqlConnection conn, Guid campaignId, string title, string progress, string reviewStatus,
        string? dmNotes = null, string secretsJson = "[]") =>
        ExecAsync(conn,
            """
            INSERT INTO story_beats
                (id, campaign_id, "order", title, objective, exit_conditions, monster_refs, npc_refs,
                 secrets, dm_notes, progress, review_status)
            VALUES
                (@id, @c, 0, @title, NULL, ARRAY[]::text[], ARRAY[]::uuid[], ARRAY[]::uuid[],
                 @secrets::jsonb, @dmNotes, @progress, @reviewStatus)
            """,
            ("id", Guid.NewGuid()), ("c", campaignId), ("title", title), ("secrets", secretsJson),
            ("dmNotes", (object?)dmNotes ?? DBNull.Value), ("progress", progress), ("reviewStatus", reviewStatus));

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
            Email = $"assistant-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Assistant {label}",
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
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)",
            ("ids", new[] { _campaignId, _otherCampaignId, _noKeyCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId, _noKeyDmUserId }));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record Activity(string ToolUseId, string Tool, string Summary, string Status);

    private sealed record PendingCard(string ToolUseId, string Tool, string Description, string? EditableText);

    private sealed record TurnResponse(
        string Status, Guid RunId, Guid? MessageId, string Text, List<Activity> Activity, List<PendingCard> Pending);

    private sealed record MessageItem(
        Guid Id, string Role, string Content, Guid? SessionId, List<Activity> Activity, DateTimeOffset CreatedAt);

    private sealed record HistoryResponse(List<MessageItem> Messages, TurnResponse? PendingRun);
}

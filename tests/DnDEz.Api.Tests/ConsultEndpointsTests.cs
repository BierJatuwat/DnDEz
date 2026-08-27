using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint D Part 2/3 — tool-calling loop + approval gate: narration → tool_use (roll_dice) →
/// (ตัดสินจาก AutonomySetting ว่า auto-execute หรือรอ DM approve) → tool_result → narration
/// ผ่านคีย์ BYOK ของ DM (Sprint D Part 1)
///
/// ขอบเขต (ดู PROJECT-CONTEXT §4 "Sprint D" Part 2/3):
///  • เรียกได้เฉพาะ DM (campaign owner) เท่านั้น — ผูกกับ RLS ของ api_keys ไม่ใช่แค่ product design
///  • จำกัด 1 รอบ tool round-trip ต่อการ resolve หนึ่งครั้ง
///  • ไม่มีแถว AutonomySetting เลย = default Suggest (ยังไม่มี campaign ไหน seed แถวนี้เองในระบบนี้)
///  • **ห้ามยิง HTTP จริงไปหา Anthropic ในเทสต์เด็ดขาด** — สลับ `IAnthropicClient` เป็น
///    `FakeAnthropicClient` ทั้งไฟล์ (ดู tests/DnDEz.Api.Tests/Fakes/FakeAnthropicClient.cs)
///
/// contract:
///
/// POST /campaigns/{campaignId}/sessions/{sessionId}/consult  [Authorize]
///   body { Content (required, ไม่เกิน 4000 ตัวอักษรหลัง trim) }
///   200 OK + { MessageId?, Content, ToolCall: null | {...}, Pending: null | { ProposalId, ToolName, ToolInput } }
///     • ไม่มี tool call เลย → MessageId มีค่า, ToolCall=null, Pending=null
///     • Autonomous + tool call → MessageId มีค่า, ToolCall มีค่า, Pending=null (execute ทันที)
///     • Suggest + tool call → MessageId=null, ToolCall=null, Pending มีค่า (รอ DM resolve)
///     • Manual → ไม่มีทางได้ ToolCall/Pending เลย (ไม่เสนอ tool ให้ Claude ตั้งแต่ต้น)
///   400/403/404 เหมือนเดิม
///
/// POST /campaigns/{campaignId}/proposals/{proposalId}/resolve  [Authorize]
///   body { Action: "approve" | "reject" }
///   200 OK + { ProposalId, Status, MessageId?, Content?, ToolCall? }
///   404 เมื่อไม่ใช่สมาชิก/proposal ไม่มีอยู่หรือเป็นของแคมเปญอื่น · 403 เมื่อไม่ใช่ DM
///   409 เมื่อ proposal ถูก resolve ไปแล้ว (ไม่ใช่ Pending อีกต่อไป)
/// </summary>
public class ConsultEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "consult-endpoint-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-consult-tests-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    // แคมเปญหลัก — RulesCombat=Autonomous (ทดสอบ auto-execute ทันทีเหมือน Part 2 เดิม)
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sessionInOtherCampaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();

    private readonly Guid _campaignNoKeyId = Guid.NewGuid();
    private readonly Guid _sessionNoKeyId = Guid.NewGuid();

    // ไม่ seed AutonomySetting เลย — ทดสอบว่า default (ไม่มีแถว) กลายเป็น Suggest จริง
    private readonly Guid _campaignSuggestId = Guid.NewGuid();
    private readonly Guid _sessionSuggestId = Guid.NewGuid();

    // RulesCombat=Manual — ไม่เสนอ tool ให้ Claude เลย
    private readonly Guid _campaignManualId = Guid.NewGuid();
    private readonly Guid _sessionManualId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fakeAnthropicClient = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;
    private Guid _dmNoKeyUserId;
    private string _dmNoKeyToken = null!;

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
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");
        (_dmNoKeyUserId, _dmNoKeyToken) = await RegisterAsync("dm-no-key");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Consult Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"Consult Other Campaign {_suffix}");
        await InsertCampaignAsync(conn, _campaignNoKeyId, _dmNoKeyUserId, $"Consult No-Key Campaign {_suffix}");
        await InsertCampaignAsync(conn, _campaignSuggestId, _dmUserId, $"Consult Suggest Campaign {_suffix}");
        await InsertCampaignAsync(conn, _campaignManualId, _dmUserId, $"Consult Manual Campaign {_suffix}");

        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignNoKeyId, _dmNoKeyUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignSuggestId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignSuggestId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignManualId, _dmUserId, "Dm");

        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _sessionInOtherCampaignId, _otherCampaignId);
        await InsertSessionAsync(conn, _sessionNoKeyId, _campaignNoKeyId);
        await InsertSessionAsync(conn, _sessionSuggestId, _campaignSuggestId);
        await InsertSessionAsync(conn, _sessionManualId, _campaignManualId);

        // แคมเปญหลักตั้งเป็น Autonomous ชัดเจน (Part 2 เดิมทดสอบ auto-execute — ต้องตั้งค่าเองแล้ว
        // เพราะ default ตอนนี้ (ไม่มีแถว) กลายเป็น Suggest ตาม Part 3) · _campaignSuggestId ตั้งใจ
        // ไม่ seed อะไรเลยเพื่อทดสอบ default-to-Suggest ตรง ๆ
        await InsertAutonomySettingAsync(conn, _campaignId, "RulesCombat", "Autonomous");
        await InsertAutonomySettingAsync(conn, _campaignManualId, "RulesCombat", "Manual");

        // ตั้ง API key ของ DM ให้พร้อมใช้ก่อนทุกเทสต์ (ผ่าน endpoint จริงของ Part 1 ไม่ seed ตรง DB
        // เพราะต้องผ่านการเข้ารหัสจริงด้วย IDataProtectionProvider ตัวเดียวกับที่ endpoint นี้จะถอด)
        var putKeyResponse = await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey });
        putKeyResponse.EnsureSuccessStatusCode();
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

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role)
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

    private static async Task InsertAutonomySettingAsync(
        NpgsqlConnection conn, Guid campaignId, string subsystem, string level)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO autonomy_settings (id, campaign_id, subsystem, level)
            VALUES (@id, @campaignId, @subsystem, @level)
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("subsystem", subsystem);
        cmd.Parameters.AddWithValue("level", level);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[]
            {
                _campaignId, _otherCampaignId, _campaignNoKeyId, _campaignSuggestId, _campaignManualId,
            });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        var userIds = new[] { _dmUserId, _playerUserId, _outsiderUserId, _dmNoKeyUserId }
            .Where(id => id != Guid.Empty).ToArray();
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", userIds);
            await deleteUsers.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"consult-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Consult {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HubConnection BuildConnection(string? token)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/campaign"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = _factory.Server.CreateWebSocketClient();
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
    }

    private async Task<long> CountDiceRollsAsync(Guid sessionId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM dice_rolls WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountMessagesAsync(Guid sessionId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM messages WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadProposalStatusAsync(Guid proposalId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM proposals WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", proposalId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    // ── happy path — no tool call ───────────────────────────────────────

    [Fact]
    public async Task Consult_by_dm_without_dice_keyword_returns_narration_with_no_tool_call()
    {
        var before = await CountDiceRollsAsync(_sessionId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "The party opens the tavern door." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Contains("The party opens the tavern door.", body!.Content);
        Assert.Null(body.ToolCall);
        Assert.Null(body.Pending);
        Assert.NotNull(body.MessageId);

        Assert.Equal(before, await CountDiceRollsAsync(_sessionId));
        Assert.Equal(2, await CountMessagesAsync(_sessionId));
    }

    // ── happy path — Autonomous level: tool call executes immediately ──────

    [Fact]
    public async Task Consult_with_roll_keyword_under_autonomous_invokes_tool_immediately()
    {
        var before = await CountDiceRollsAsync(_sessionId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "The rogue attempts to roll for stealth." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("seed", rawJson, StringComparison.OrdinalIgnoreCase);

        var body = JsonSerializer.Deserialize<ConsultResponse>(rawJson, JsonOptions);
        Assert.NotNull(body);
        Assert.NotNull(body!.MessageId);
        Assert.Null(body.Pending);
        Assert.NotNull(body.ToolCall);
        Assert.Equal("roll_dice", body.ToolCall!.Name);
        Assert.Contains("total", body.ToolCall.Result, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before + 1, await CountDiceRollsAsync(_sessionId));

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT expression, seed, character_id FROM dice_rolls WHERE session_id = @sessionId ORDER BY created_at DESC LIMIT 1",
            conn);
        select.Parameters.AddWithValue("sessionId", _sessionId);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("1d20+5", reader.GetString(0));
        Assert.False(await reader.IsDBNullAsync(1)); // seed เก็บลง DB จริงตามข้อบังคับ provenance
        Assert.True(await reader.IsDBNullAsync(2)); // ไม่ผูก character — เป็นการทอยของ AI/DM เอง
    }

    [Fact]
    public async Task Consult_message_history_is_built_correctly_from_prior_session_messages()
    {
        var first = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "First message in this session." });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "Second message in this session." });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var history = _fakeAnthropicClient.LastHistoryReceived;
        Assert.NotNull(history);
        Assert.Equal(3, history!.Count); // [dm#1(user), ai#1(assistant), dm#2(user)]
        Assert.Equal("user", history[0].Role);
        Assert.Contains("First message", history[0].Text);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("user", history[2].Role);
        Assert.Contains("Second message", history[2].Text);
    }

    [Fact]
    public async Task Consult_with_malformed_tool_input_returns_200_with_error_in_tool_result_not_500()
    {
        var beforeDiceRolls = await CountDiceRollsAsync(_sessionId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "roll malformed-tool-input please" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body!.ToolCall);
        Assert.Contains("Error", body.ToolCall!.Result, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(beforeDiceRolls, await CountDiceRollsAsync(_sessionId));
    }

    // ── Manual level — tool never offered at all ────────────────────────

    [Fact]
    public async Task Consult_under_manual_level_never_offers_tool_even_with_roll_keyword()
    {
        var before = await CountDiceRollsAsync(_sessionManualId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignManualId}/sessions/{_sessionManualId}/consult",
            new { Content = "Please roll for initiative." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Null(body!.ToolCall);
        Assert.Null(body.Pending);
        Assert.NotNull(body.MessageId);

        // Manual = ไม่มีทาง execute tool ได้เลยไม่ว่า content จะพูดถึง "roll" แค่ไหน
        Assert.Equal(before, await CountDiceRollsAsync(_sessionManualId));
    }

    // ── Suggest level (default เมื่อไม่มีแถว AutonomySetting) — pending proposal ──

    [Fact]
    public async Task Consult_under_default_suggest_level_creates_pending_proposal_not_executing_tool()
    {
        var beforeDiceRolls = await CountDiceRollsAsync(_sessionSuggestId);
        var beforeMessages = await CountMessagesAsync(_sessionSuggestId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "The rogue wants to roll for stealth." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Null(body!.MessageId); // ยังไม่มี narration สุดท้ายจนกว่าจะ resolve
        Assert.Null(body.ToolCall);
        Assert.NotNull(body.Pending);
        Assert.Equal("roll_dice", body.Pending!.ToolName);
        Assert.NotEqual(Guid.Empty, body.Pending.ProposalId);

        // ห้ามทอยดักไว้ล่วงหน้าเด็ดขาด — ต้องรอ approve จริงก่อน
        Assert.Equal(beforeDiceRolls, await CountDiceRollsAsync(_sessionSuggestId));
        // แค่ข้อความ DM ที่ส่งเข้ามาถูก persist (ยังไม่มี AI message จนกว่าจะ resolve)
        Assert.Equal(beforeMessages + 1, await CountMessagesAsync(_sessionSuggestId));

        Assert.Equal("Pending", await ReadProposalStatusAsync(body.Pending.ProposalId));
    }

    [Fact]
    public async Task Consult_pending_proposal_broadcasts_to_joined_campaign_group()
    {
        await using var connection = BuildConnection(_dmToken);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ProposalCreated", payload => received.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignSuggestId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "Time to roll for perception." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);
        var broadcast = await received.Task;

        Assert.Equal(body!.Pending!.ProposalId, broadcast.GetProperty("proposalId").GetGuid());
        Assert.Equal("roll_dice", broadcast.GetProperty("toolName").GetString());

        await connection.StopAsync();
    }

    // ── resolve proposal ─────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_proposal_with_approve_executes_tool_and_persists_narration()
    {
        var consultResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "Approve-test: roll for initiative." });
        var consultBody = await consultResponse.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        var proposalId = consultBody!.Pending!.ProposalId;

        var beforeDiceRolls = await CountDiceRollsAsync(_sessionSuggestId);
        var beforeMessages = await CountMessagesAsync(_sessionSuggestId);

        var resolveResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve",
            new { Action = "approve" });

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var rawJson = await resolveResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("seed", rawJson, StringComparison.OrdinalIgnoreCase);

        var resolveBody = JsonSerializer.Deserialize<ProposalResolutionResponse>(rawJson, JsonOptions);
        Assert.Equal("Approved", resolveBody!.Status);
        Assert.NotNull(resolveBody.MessageId);
        Assert.NotNull(resolveBody.ToolCall);
        Assert.Equal("roll_dice", resolveBody.ToolCall!.Name);

        Assert.Equal(beforeDiceRolls + 1, await CountDiceRollsAsync(_sessionSuggestId));
        Assert.Equal(beforeMessages + 1, await CountMessagesAsync(_sessionSuggestId));
        Assert.Equal("Approved", await ReadProposalStatusAsync(proposalId));
    }

    [Fact]
    public async Task Resolve_proposal_with_reject_does_not_execute_tool_or_add_narration()
    {
        var consultResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "Reject-test: roll for initiative." });
        var consultBody = await consultResponse.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        var proposalId = consultBody!.Pending!.ProposalId;

        var beforeDiceRolls = await CountDiceRollsAsync(_sessionSuggestId);
        var beforeMessages = await CountMessagesAsync(_sessionSuggestId);

        var resolveResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve",
            new { Action = "reject" });

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var resolveBody = await resolveResponse.Content.ReadFromJsonAsync<ProposalResolutionResponse>(JsonOptions);
        Assert.Equal("Rejected", resolveBody!.Status);
        Assert.Null(resolveBody.MessageId);
        Assert.Null(resolveBody.ToolCall);

        Assert.Equal(beforeDiceRolls, await CountDiceRollsAsync(_sessionSuggestId));
        Assert.Equal(beforeMessages, await CountMessagesAsync(_sessionSuggestId));
        Assert.Equal("Rejected", await ReadProposalStatusAsync(proposalId));
    }

    [Fact]
    public async Task Resolve_proposal_with_malformed_tool_input_returns_200_with_error_not_500()
    {
        var consultResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "roll malformed-tool-input via suggest please" });
        var consultBody = await consultResponse.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        var proposalId = consultBody!.Pending!.ProposalId;

        var beforeDiceRolls = await CountDiceRollsAsync(_sessionSuggestId);

        var resolveResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var resolveBody = await resolveResponse.Content.ReadFromJsonAsync<ProposalResolutionResponse>(JsonOptions);
        Assert.NotNull(resolveBody!.ToolCall);
        Assert.Contains("Error", resolveBody.ToolCall!.Result, StringComparison.OrdinalIgnoreCase);

        // input ผิดรูปแบบตั้งแต่ตอนสร้าง proposal — approve แล้วก็ยังไม่มีการทอยจริงเกิดขึ้นเลย
        Assert.Equal(beforeDiceRolls, await CountDiceRollsAsync(_sessionSuggestId));
    }

    [Fact]
    public async Task Resolve_proposal_that_is_already_resolved_returns_409()
    {
        var consultResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "Double-resolve-test: roll for initiative." });
        var consultBody = await consultResponse.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        var proposalId = consultBody!.Pending!.ProposalId;

        var first = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "reject" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Resolve_proposal_by_member_who_is_not_dm_returns_403()
    {
        var consultResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "Non-owner-resolve-test: roll for initiative." });
        var consultBody = await consultResponse.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        var proposalId = consultBody!.Pending!.ProposalId;

        // _playerUserId เป็นสมาชิกแคมเปญ Suggest จริง (มองเห็นแคมเปญได้ผ่าน RLS) แต่ไม่ใช่ DM/owner
        var playerResponse = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });
        Assert.Equal(HttpStatusCode.Forbidden, playerResponse.StatusCode);

        // outsider ไม่ใช่สมาชิกเลย ต้องได้ 404 (มองไม่เห็นแคมเปญนี้ผ่าน RLS) — คนละเหตุผลจาก 403 ข้างบน
        var outsiderResponse = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });
        Assert.Equal(HttpStatusCode.NotFound, outsiderResponse.StatusCode);
    }

    [Fact]
    public async Task Resolve_proposal_with_invalid_action_returns_400()
    {
        var consultResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/consult",
            new { Content = "Invalid-action-test: roll for initiative." });
        var consultBody = await consultResponse.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        var proposalId = consultBody!.Pending!.ProposalId;

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "maybe" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_unknown_proposal_returns_404()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{Guid.NewGuid()}/resolve", new { Action = "approve" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── validation (400 — ห้ามแตะ DB/Anthropic เลย) ─────────────────────

    [Fact]
    public async Task Consult_with_empty_content_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult", new { Content = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Consult_with_content_too_long_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = new string('a', 4001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Consult_when_dm_has_no_active_api_key_returns_400_with_reason()
    {
        var response = await AuthedClient(_dmNoKeyToken).PostAsJsonAsync(
            $"/campaigns/{_campaignNoKeyId}/sessions/{_sessionNoKeyId}/consult",
            new { Content = "This should fail — no key set." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultErrorResponse>(JsonOptions);
        Assert.Equal("dm_api_key_not_set", body!.Error);
    }

    // ── authorization ───────────────────────────────────────────────────

    [Fact]
    public async Task Consult_by_non_owner_member_returns_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "Player trying to consult the AI directly." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Consult_by_non_member_returns_404()
    {
        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "Outsider trying to consult." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Consult_with_session_from_another_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionInOtherCampaignId}/consult",
            new { Content = "Session belongs to a different campaign." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Consult_without_jwt_returns_401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "No auth header." });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record ConsultToolCallResponse(string Name, JsonElement Input, string Result);

    private record ConsultPendingResponse(Guid ProposalId, string ToolName, JsonElement ToolInput);

    private record ConsultResponse(
        Guid? MessageId, string Content, ConsultToolCallResponse? ToolCall, ConsultPendingResponse? Pending);

    private record ConsultErrorResponse(string Error);

    private record ProposalResolutionResponse(
        Guid ProposalId, string Status, Guid? MessageId, string? Content, ConsultToolCallResponse? ToolCall);
}

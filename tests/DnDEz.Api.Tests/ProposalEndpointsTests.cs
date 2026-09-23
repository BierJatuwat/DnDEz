using System.Net;
using System.Net.Http.Headers;
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
/// คิว proposal ของเทิร์นผู้เล่น: <c>POST /campaigns/{c}/proposals/{p}/resolve</c> +
/// <c>GET /campaigns/{c}/sessions/{s}/proposals</c>
///
/// **2026-09-23** — เดิมเทสต์พวกนี้อยู่ใน `ConsultEndpointsTests.cs` และสร้าง proposal ผ่าน `POST
/// /consult` (ช่องที่ DM คุยกับ AI ซึ่งถูกลบไปแล้ว แทนที่ด้วยผู้ช่วย DM ช่องเดียว) ย้ายมาที่นี่พร้อมเปลี่ยน
/// วิธีสร้าง proposal เป็นเทิร์นผู้เล่นจริง (`POST /actions` ภายใต้ autonomy dial ระดับ Suggest) ซึ่งเป็น
/// ทางเดียวที่เหลืออยู่ที่สร้างแถวในตาราง `proposals` — พฤติกรรมของ resolve/list เองไม่เปลี่ยน
///
/// contract:
///   POST .../proposals/{proposalId}/resolve  [Authorize, DM-only]
///     body { Action: "approve" | "reject" }
///     200 OK + { ProposalId, Status, MessageId?, Content?, ToolCall? }
///     404 เมื่อไม่ใช่สมาชิก/proposal ไม่มีอยู่หรือเป็นของแคมเปญอื่น · 403 เมื่อไม่ใช่ DM
///     409 เมื่อ proposal ถูก resolve ไปแล้ว
///   GET .../sessions/{sessionId}/proposals  [Authorize, DM-only]
///     200 OK + pending proposals เรียงเก่า→ใหม่
/// </summary>
public class ProposalEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "proposal-endpoint-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-proposal-tests-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    // ไม่ seed AutonomySetting เลย — default Suggest (ทุก proposal ในไฟล์นี้มาจากแคมเปญนี้)
    private readonly Guid _campaignSuggestId = Guid.NewGuid();
    private readonly Guid _sessionSuggestId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid(); // เป็นของ _playerUserId

    // แคมเปญอื่นของ DM คนเดียวกัน — ใช้ทดสอบ scoping ข้ามแคมเปญ/เซสชัน
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fakeAnthropicClient = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

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

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignSuggestId, _dmUserId, $"Proposal Suggest Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"Proposal Other Campaign {_suffix}");

        await InsertMembershipAsync(conn, _campaignSuggestId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignSuggestId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");

        await InsertSessionAsync(conn, _sessionSuggestId, _campaignSuggestId);
        await InsertSessionAsync(conn, _otherSessionId, _otherCampaignId);

        await InsertCharacterAsync(conn, _heroId, _campaignSuggestId, _playerUserId, "Suggest Hero");

        var putKeyResponse = await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey });
        putKeyResponse.EnsureSuccessStatusCode();
    }

    // ── resolve ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_proposal_with_approve_executes_tool_and_persists_narration()
    {
        var proposalId = await CreateProposalAsync("Approve-test: I roll for initiative.");
        var systemPromptFromFirstTurn = _fakeAnthropicClient.LastSystemPromptReceived;

        var beforeDiceRolls = await CountDiceRollsAsync(_sessionSuggestId);
        var beforeMessages = await CountMessagesAsync(_sessionSuggestId);

        var resolveResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });

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

        // resolve ต้อง replay ด้วย system prompt เดียวกับตอน SendTurnAsync รอบแรกเป๊ะ (เก็บผ่าน
        // Proposal.ToolCallJson) ไม่ใช่ประกอบใหม่ตอน resolve ที่ roster อาจเปลี่ยนไปแล้ว
        Assert.NotNull(systemPromptFromFirstTurn);
        Assert.Equal(systemPromptFromFirstTurn, _fakeAnthropicClient.LastSystemPromptReceived);
    }

    [Fact]
    public async Task Resolve_proposal_with_reject_does_not_execute_tool_or_add_narration()
    {
        var proposalId = await CreateProposalAsync("Reject-test: I roll for initiative.");

        var beforeDiceRolls = await CountDiceRollsAsync(_sessionSuggestId);
        var beforeMessages = await CountMessagesAsync(_sessionSuggestId);

        var resolveResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "reject" });

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
        var proposalId = await CreateProposalAsync("I roll malformed-tool-input via suggest please.");

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
        var proposalId = await CreateProposalAsync("Double-resolve-test: I roll for initiative.");

        var first = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "reject" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>พบระหว่าง retroactive review (Sprint D Part 3) — เดิม read-then-write เช็ค
    /// proposal.Status ไม่ atomic เลย สอง resolve พร้อมกันอาจ execute tool ซ้ำสองรอบ (double dice
    /// roll + double Anthropic call ที่ควรเกิดครั้งเดียว) — เทสต์นี้ยิงสอง approve พร้อมกันจริงผ่าน
    /// Task.WhenAll ยืนยันว่ามีแค่ผู้ชนะรายเดียวที่ execute จริง</summary>
    [Fact]
    public async Task Resolve_proposal_two_concurrent_approves_only_one_executes_tool()
    {
        var proposalId = await CreateProposalAsync("Concurrent-resolve-test: I roll for initiative.");

        var beforeDiceRolls = await CountDiceRollsAsync(_sessionSuggestId);

        var task1 = AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });
        var task2 = AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "approve" });

        var responses = await Task.WhenAll(task1, task2);

        // ต้องมีผู้ชนะเป๊ะ ๆ 1 ราย (200 OK) อีกรายแพ้ race ได้ 409 Conflict — ไม่มีทางทั้งคู่ 200
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // execute tool ครั้งเดียวเท่านั้น — ไม่ใช่สองครั้ง
        Assert.Equal(beforeDiceRolls + 1, await CountDiceRollsAsync(_sessionSuggestId));
        Assert.Equal("Approved", await ReadProposalStatusAsync(proposalId));
    }

    [Fact]
    public async Task Resolve_proposal_by_member_who_is_not_dm_returns_403()
    {
        var proposalId = await CreateProposalAsync("Non-owner-resolve-test: I roll for initiative.");

        // _playerUserId เป็นสมาชิกแคมเปญนี้จริง (มองเห็นแคมเปญได้ผ่าน RLS) แต่ไม่ใช่ DM/owner
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
        var proposalId = await CreateProposalAsync("Invalid-action-test: I roll for initiative.");

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

    // ── list pending proposals ──────────────────────────────────────────

    [Fact]
    public async Task List_proposals_returns_pending_proposals_oldest_first_with_matching_fields()
    {
        var firstId = await CreateProposalAsync("List-test first: I roll for initiative.");
        var secondId = await CreateProposalAsync("List-test second: I roll for perception.");

        var response = await AuthedClient(_dmToken).GetAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/proposals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ProposalListItem>>(JsonOptions);
        Assert.NotNull(list);

        var ids = list!.Select(p => p.Id).ToList();
        var firstIndex = ids.IndexOf(firstId);
        var secondIndex = ids.IndexOf(secondId);
        Assert.True(firstIndex >= 0 && secondIndex >= 0);
        Assert.True(firstIndex < secondIndex); // oldest first

        var secondItem = list.Single(p => p.Id == secondId);
        Assert.Equal(_sessionSuggestId, secondItem.SessionId);
        Assert.Equal("RulesCombat", secondItem.Subsystem);
        Assert.Equal("roll_dice", secondItem.ToolName);
        Assert.Equal("Pending", secondItem.Status);
        Assert.Equal("1d20+5", secondItem.ToolInput.GetProperty("expression").GetString());
    }

    [Fact]
    public async Task List_proposals_excludes_resolved_proposals()
    {
        var proposalId = await CreateProposalAsync("List-resolved-test: I roll for initiative.");

        var resolveResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/proposals/{proposalId}/resolve", new { Action = "reject" });
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);

        var response = await AuthedClient(_dmToken).GetAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/proposals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ProposalListItem>>(JsonOptions);
        Assert.NotNull(list);
        Assert.DoesNotContain(list!, p => p.Id == proposalId);
    }

    [Fact]
    public async Task List_proposals_by_non_owner_member_returns_403()
    {
        var response = await AuthedClient(_playerToken).GetAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/proposals");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_proposals_for_unknown_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).GetAsync(
            $"/campaigns/{Guid.NewGuid()}/sessions/{_sessionSuggestId}/proposals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_proposals_for_session_from_another_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).GetAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_otherSessionId}/proposals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_proposals_does_not_return_proposal_belonging_to_a_different_session()
    {
        var proposalId = await CreateProposalAsync("List-other-session-test: I roll for initiative.");

        // ถามหา proposal ของ _sessionSuggestId ผ่าน session อื่น (_otherSessionId ในแคมเปญอื่น) — ต้อง
        // ได้ 200 + list ว่างเปล่า (ไม่ใช่ 404 — แคมเปญ/เซสชันนี้มีอยู่จริง แค่ proposal ไม่ได้อยู่ที่นี่)
        // ยืนยันว่า scoping ผูกกับ sessionId ไม่ใช่แค่ campaignId เฉย ๆ
        var response = await AuthedClient(_dmToken).GetAsync(
            $"/campaigns/{_otherCampaignId}/sessions/{_otherSessionId}/proposals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ProposalListItem>>(JsonOptions);
        Assert.NotNull(list);
        Assert.DoesNotContain(list!, p => p.Id == proposalId);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>สร้าง proposal ผ่านเทิร์นผู้เล่นจริง (`/actions`) — แคมเปญ Suggest ในไฟล์นี้ไม่เคย seed
    /// AutonomySetting เลย จึง default เป็น Suggest เสมอ (ดู doc comment ของคลาส)</summary>
    private async Task<Guid> CreateProposalAsync(string content)
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/actions",
            new { CharacterId = _heroId, Content = content });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ActionResponse>(JsonOptions);
        Assert.NotNull(body!.Pending);
        return body.Pending!.ProposalId;
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

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, Guid ownerUserId, string name)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, damage_resistances, damage_vulnerabilities, damage_immunities, level,
                 controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}', 20, 20, 0, 10, ARRAY[]::text[],
                 ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", ownerUserId);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"proposal-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Proposal {label}",
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

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignSuggestId, _otherCampaignId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    private record AuthResponse(string Token, Guid UserId);

    private record ConsultToolCallResponse(string Name, JsonElement Input, string Result);

    private record ConsultPendingResponse(Guid ProposalId, string ToolName, JsonElement ToolInput);

    private record ActionResponse(Guid? MessageId, string Content, ConsultToolCallResponse? ToolCall, ConsultPendingResponse? Pending);

    private record ProposalResolutionResponse(
        Guid ProposalId, string Status, Guid? MessageId, string? Content, ConsultToolCallResponse? ToolCall);

    private record ProposalListItem(
        Guid Id,
        Guid SessionId,
        string Subsystem,
        string ToolName,
        JsonElement ToolInput,
        string? Reason,
        string Status,
        DateTimeOffset CreatedAt);
}

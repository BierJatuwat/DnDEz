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
/// Sprint D Part 2 — tool-calling loop skeleton: narration → tool_use (roll_dice) → tool_result →
/// narration ผ่านคีย์ BYOK ของ DM (Sprint D Part 1) ยังไม่มี approval gate เลย (auto-allow เสมอ —
/// Part 3 ถึงจะมี gate จริง)
///
/// ขอบเขต (ดู PROJECT-CONTEXT §4 "Sprint D" Part 2):
///  • เรียกได้เฉพาะ DM (campaign owner) เท่านั้น — ผูกกับ RLS ของ api_keys ไม่ใช่แค่ product design
///  • จำกัด 1 รอบ tool round-trip
///  • **ห้ามยิง HTTP จริงไปหา Anthropic ในเทสต์เด็ดขาด** — สลับ `IAnthropicClient` เป็น
///    `FakeAnthropicClient` ทั้งไฟล์ (ดู tests/DnDEz.Api.Tests/Fakes/FakeAnthropicClient.cs)
///
/// contract:
///
/// POST /campaigns/{campaignId}/sessions/{sessionId}/consult  [Authorize]
///   body { Content (required, ไม่เกิน 4000 ตัวอักษรหลัง trim) }
///   200 OK + { MessageId, Content, ToolCall: null | { Name, Input, Result } }
///   400 เมื่อ Content ว่าง/ยาวเกิน หรือ DM ยังไม่ตั้ง Active API key (`{ Error: "dm_api_key_not_set" }`)
///   403 เมื่อผู้เรียกเป็นสมาชิกแคมเปญแต่ไม่ใช่ DM (campaign owner)
///   404 เมื่อไม่ใช่สมาชิกแคมเปญเลย/session ไม่มีอยู่หรือเป็นของแคมเปญอื่น
/// </summary>
public class ConsultEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "consult-endpoint-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-consult-tests-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sessionInOtherCampaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();

    private readonly Guid _campaignNoKeyId = Guid.NewGuid();
    private readonly Guid _sessionNoKeyId = Guid.NewGuid();

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

        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignNoKeyId, _dmNoKeyUserId, "Dm");

        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _sessionInOtherCampaignId, _otherCampaignId);
        await InsertSessionAsync(conn, _sessionNoKeyId, _campaignNoKeyId);

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

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _otherCampaignId, _campaignNoKeyId });
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

        // ไม่มี tool call = ไม่มี dice roll เกิดขึ้นจริงเลย
        Assert.Equal(before, await CountDiceRollsAsync(_sessionId));

        // ต้อง persist ทั้งข้อความ DM ที่ส่งเข้ามาและ narration ของ AI (2 แถวใหม่)
        Assert.Equal(2, await CountMessagesAsync(_sessionId));
    }

    // ── happy path — with tool call ─────────────────────────────────────

    [Fact]
    public async Task Consult_with_roll_keyword_invokes_roll_dice_tool_and_persists_real_dice_roll()
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
        Assert.NotNull(body!.ToolCall);
        Assert.Equal("roll_dice", body.ToolCall!.Name);
        Assert.Contains("total", body.ToolCall.Result, StringComparison.OrdinalIgnoreCase);

        // tool call ต้อง persist เป็น DiceRoll แถวจริงเสมอ (PROJECT-CONTEXT §3)
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

        // input ผิดรูปแบบ = ไม่มีการทอยจริงเกิดขึ้นเลย (ไม่ควรมี DiceRoll แถวใหม่)
        Assert.Equal(beforeDiceRolls, await CountDiceRollsAsync(_sessionId));
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

    private record ConsultResponse(Guid MessageId, string Content, ConsultToolCallResponse? ToolCall);

    private record ConsultErrorResponse(string Error);
}

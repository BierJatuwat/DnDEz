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
/// Sprint E Part 3 — player action input: <c>POST /campaigns/{id}/sessions/{id}/actions</c>.
/// Reuses the same orchestrator pipeline as `/consult` (see `OrchestratorTurnRunner`) but with
/// player-facing authorization and a group-wide `ActionMessageCreated` broadcast.
///
/// contract:
///   body { CharacterId, Content (required, ≤4000 chars after trim) }
///   200 OK + same shape as ConsultResponse (MessageId?, Content, ToolCall, Pending)
///   403 เมื่อ caller ไม่ใช่เจ้าของ CharacterId · 404 เมื่อ campaign/session/character ไม่มีอยู่จริง
///   หรือ character ไม่ได้อยู่ campaign นี้ · 400 (dm_api_key_not_set) เมื่อ DM ยังไม่ได้ตั้ง key
///   broadcasts "ActionMessageCreated" เข้ากลุ่มแคมเปญทั้งกลุ่ม (ไม่ใช่ DM-only) — ต่างจาก /consult
///   ที่ไม่ broadcast อะไรเลย (ดู ConsultEndpointsTests.Consult_never_broadcasts_ActionMessageCreated...)
/// </summary>
public class PlayerActionEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "player-action-endpoint-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-player-action-tests-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    // แคมเปญหลัก — RulesCombat=Autonomous
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _characterId = Guid.NewGuid(); // เป็นของ _playerUserId
    private readonly Guid _unownedCharacterId = Guid.NewGuid(); // เป็นของ _otherPlayerUserId

    // ไม่ seed AutonomySetting เลย — default Suggest
    private readonly Guid _campaignSuggestId = Guid.NewGuid();
    private readonly Guid _sessionSuggestId = Guid.NewGuid();
    private readonly Guid _characterSuggestId = Guid.NewGuid();

    // DM ไม่เคยตั้ง API key เลย
    private readonly Guid _campaignNoKeyId = Guid.NewGuid();
    private readonly Guid _sessionNoKeyId = Guid.NewGuid();
    private readonly Guid _characterNoKeyId = Guid.NewGuid();

    // ตัวละครที่อยู่คนละแคมเปญ — ใช้ทดสอบ 404
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _characterInOtherCampaignId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fakeAnthropicClient = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _otherPlayerUserId;
    private string _otherPlayerToken = null!;
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
        (_otherPlayerUserId, _otherPlayerToken) = await RegisterAsync("other-player");
        (_dmNoKeyUserId, _dmNoKeyToken) = await RegisterAsync("dm-no-key");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"PA Campaign {_suffix}");
        await InsertCampaignAsync(conn, _campaignSuggestId, _dmUserId, $"PA Suggest Campaign {_suffix}");
        await InsertCampaignAsync(conn, _campaignNoKeyId, _dmNoKeyUserId, $"PA No-Key Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"PA Other Campaign {_suffix}");

        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignId, _otherPlayerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignSuggestId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignSuggestId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignNoKeyId, _dmNoKeyUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignNoKeyId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");

        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _sessionSuggestId, _campaignSuggestId);
        await InsertSessionAsync(conn, _sessionNoKeyId, _campaignNoKeyId);

        await InsertAutonomySettingAsync(conn, _campaignId, "RulesCombat", "Autonomous");
        // _campaignSuggestId: ไม่ seed อะไรเลย -> default Suggest
        // _campaignNoKeyId: ไม่ต้องตั้ง autonomy เพราะ request จะโดน 400 ที่ api-key check ก่อนถึงจุดนั้น

        await InsertCharacterAsync(conn, _characterId, _campaignId, _playerUserId, "Hero");
        await InsertCharacterAsync(conn, _unownedCharacterId, _campaignId, _otherPlayerUserId, "Not Yours");
        await InsertCharacterAsync(conn, _characterSuggestId, _campaignSuggestId, _playerUserId, "Suggest Hero");
        await InsertCharacterAsync(conn, _characterNoKeyId, _campaignNoKeyId, _playerUserId, "No Key Hero");
        await InsertCharacterAsync(
            conn, _characterInOtherCampaignId, _otherCampaignId, _playerUserId, "Wrong Campaign Hero");

        // ตั้ง API key ของ DM หลักผ่าน endpoint จริง (ต้องเข้ารหัสจริงด้วย IDataProtectionProvider
        // ตัวเดียวกับที่ app_get_dm_api_key/PlayerActionEndpoints จะถอด) — _dmNoKeyUserId ไม่ตั้งเลยตั้งใจ
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

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[]
            {
                _campaignId, _campaignSuggestId, _campaignNoKeyId, _otherCampaignId,
            });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        var userIds = new[] { _dmUserId, _playerUserId, _otherPlayerUserId, _dmNoKeyUserId }
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
            Email = $"player-action-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"PlayerAction {label}",
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

    private async Task<long> CountMessagesAsync(Guid sessionId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM messages WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── happy path — no tool call, broadcasts both turns to the whole group ─────

    [Fact]
    public async Task PlayerAction_by_owner_without_dice_keyword_returns_narration_and_broadcasts_both_messages()
    {
        await using var connection = BuildConnection(_dmToken);
        var received = new List<JsonElement>();
        var secondReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ActionMessageCreated", payload =>
        {
            received.Add(payload);
            if (received.Count >= 2)
            {
                secondReceived.TrySetResult(true);
            }
        });

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _characterId, Content = "I search the room for traps." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotNull(body!.MessageId);
        Assert.Null(body.ToolCall);
        Assert.Null(body.Pending);

        var completed = await Task.WhenAny(secondReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(secondReceived.Task, completed);

        Assert.Equal(2, received.Count);
        Assert.Equal("Player", received[0].GetProperty("authorType").GetString());
        Assert.Equal(_characterId, received[0].GetProperty("characterId").GetGuid());
        Assert.Equal("Ai", received[1].GetProperty("authorType").GetString());

        await connection.StopAsync();
    }

    [Fact]
    public async Task PlayerAction_persists_message_with_player_author_type_and_character_id()
    {
        var before = await CountMessagesAsync(_sessionId);

        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _characterId, Content = "I inspect the ancient door." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(before + 2, await CountMessagesAsync(_sessionId)); // player message + AI narration

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT author_type, character_id, source FROM messages WHERE session_id = @sessionId ORDER BY created_at ASC LIMIT 1",
            conn);
        cmd.Parameters.AddWithValue("sessionId", _sessionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Player", reader.GetString(0));
        Assert.Equal(_characterId, reader.GetGuid(1));
        Assert.Equal("Player", reader.GetString(2));
    }

    // ── authorization ────────────────────────────────────────────────────

    [Fact]
    public async Task PlayerAction_for_character_not_owned_by_caller_returns_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _unownedCharacterId, Content = "I try to act as someone else's character." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlayerAction_for_character_in_different_campaign_returns_404()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _characterInOtherCampaignId, Content = "Wrong campaign character." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlayerAction_when_session_not_found_returns_404()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{Guid.NewGuid()}/actions",
            new { CharacterId = _characterId, Content = "No such session." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task PlayerAction_with_content_over_max_length_returns_400()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _characterId, Content = new string('a', 4001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PlayerAction_with_blank_content_returns_400()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _characterId, Content = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── DM API key resolution across the user boundary (app_get_dm_api_key) ────

    [Fact]
    public async Task PlayerAction_when_dm_has_no_api_key_returns_400()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignNoKeyId}/sessions/{_sessionNoKeyId}/actions",
            new { CharacterId = _characterNoKeyId, Content = "No key set for this DM." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultErrorResponse>(JsonOptions);
        Assert.Equal("dm_api_key_not_set", body!.Error);
    }

    // ── Suggest autonomy — pending proposal stays DM-only, only own message reaches the group ──

    [Fact]
    public async Task PlayerAction_under_suggest_creates_pending_proposal_and_broadcasts_only_own_message()
    {
        await using var connection = BuildConnection(_dmToken);
        var groupMessages = new List<JsonElement>();
        var proposalReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ActionMessageCreated", payload => groupMessages.Add(payload));
        connection.On<JsonElement>("ProposalCreated", payload => proposalReceived.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignSuggestId);

        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSuggestId}/sessions/{_sessionSuggestId}/actions",
            new { CharacterId = _characterSuggestId, Content = "I roll to sneak past the guards." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.Null(body!.MessageId); // ยังไม่มี narration สุดท้ายจนกว่า DM จะ resolve
        Assert.NotNull(body.Pending);

        var completed = await Task.WhenAny(proposalReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(proposalReceived.Task, completed);

        // ให้เวลาพอจะรับ group broadcast ถ้าจะมาจริง กัน false negative จาก timing
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(groupMessages); // แค่ข้อความของผู้เล่นเอง — narration ยังไม่มีอยู่จนกว่าจะ resolve
        Assert.Equal("Player", groupMessages[0].GetProperty("authorType").GetString());

        await connection.StopAsync();
    }

    private record AuthResponse(string Token, Guid UserId);

    private record ConsultToolCallResponse(string Name, JsonElement Input, string Result);

    private record ConsultPendingResponse(Guid ProposalId, string ToolName, JsonElement ToolInput);

    private record ConsultResponse(
        Guid? MessageId, string Content, ConsultToolCallResponse? ToolCall, ConsultPendingResponse? Pending);

    private record ConsultErrorResponse(string Error);
}

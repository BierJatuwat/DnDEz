using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 6 — fog + projection ต่อ role + SignalR group ต่อ role (design doc §13)
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// POST /campaigns/{cid}/scenes/{scid}/fog/reveal   [DM เท่านั้น] body { X, Y, Radius }
///      201? ไม่ — 200 + FogAreaResponse (Id ใหม่) · broadcast "FogChanged" ทั้ง role-group
/// POST /campaigns/{cid}/scenes/{scid}/fog/hide     [DM เท่านั้น] body { AreaId } · 200 + FogStateResponse
///
/// GET  /campaigns/{cid}/sessions/{sid}/scene       [สมาชิกทุกคนเรียกได้ — ไม่ใช่ DM-only แล้ว]
///      **DM**: เห็นทุก token ไม่สนใจหมอก, HP ทุกตัวเป็นตัวเลขจริง
///      **Player**: เห็นเฉพาะ token ที่อยู่ในพื้นที่หมอกที่เปิดแล้ว (token นอกพื้นที่ **ไม่อยู่ใน
///      JSON เลย** ไม่ใช่ null/ซ่อน) · HP ตัวละครผู้เล่นปกติ = ตัวเลขจริงเสมอ · HP ตัวละคร NPC
///      (ControlledByDm=true) เห็นเฉพาะถ้า NpcStatsVisibleToParty=true ไม่งั้น field HP เป็น null
///
/// Hub: JoinCampaign ต้องเข้า role-group ที่ถูกต้องด้วย (DM→dm group, Player→player group) —
/// ทดสอบทางอ้อมผ่าน FogChanged broadcast ที่ยิงเข้าทั้งสอง role-group
/// </summary>
public class SceneFogEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "scene-fog-endpoints-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _playerCharacterId = Guid.NewGuid();       // ControlledByDm=false
    private readonly Guid _hiddenNpcCharacterId = Guid.NewGuid();    // ControlledByDm=true, NpcStatsVisibleToParty=false
    private readonly Guid _visibleNpcCharacterId = Guid.NewGuid();   // ControlledByDm=true, NpcStatsVisibleToParty=true

    private readonly Guid _revealedTokenId = Guid.NewGuid();   // อยู่ในพื้นที่หมอกที่เปิด (0,0 r=10) — player character
    private readonly Guid _hiddenTokenId = Guid.NewGuid();     // อยู่นอกพื้นที่หมอก (999,999) — player character
    private readonly Guid _hiddenNpcTokenId = Guid.NewGuid();  // อยู่ในพื้นที่หมอกที่เปิด — NPC ปิดการมองเห็น
    private readonly Guid _visibleNpcTokenId = Guid.NewGuid(); // อยู่ในพื้นที่หมอกที่เปิด — NPC เปิดการมองเห็น

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Fog Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        await InsertCharacterAsync(conn, _playerCharacterId, _campaignId, "Hero", _playerUserId, false, true, 15, 20);
        await InsertCharacterAsync(conn, _hiddenNpcCharacterId, _campaignId, "Hidden Ogre", null, true, false, 30, 59);
        await InsertCharacterAsync(conn, _visibleNpcCharacterId, _campaignId, "Friendly Guard", null, true, true, 10, 10);

        // scene: หมอกเปิดวงเดียวที่ (0,0) รัศมี 10 — token ใน (5,5) อยู่ในวง, (999,999) อยู่นอกวง
        var tokens = $$"""
            [
                {"id":"{{_revealedTokenId}}","entityType":"Character","entityId":"{{_playerCharacterId}}","x":5,"y":5},
                {"id":"{{_hiddenTokenId}}","entityType":"Character","entityId":"{{_playerCharacterId}}","x":999,"y":999},
                {"id":"{{_hiddenNpcTokenId}}","entityType":"Character","entityId":"{{_hiddenNpcCharacterId}}","x":3,"y":3},
                {"id":"{{_visibleNpcTokenId}}","entityType":"Character","entityId":"{{_visibleNpcCharacterId}}","x":4,"y":4}
            ]
            """;
        await InsertSceneAsync(conn, _sceneId, _sessionId, tokens);
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = @id", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("id", _campaignId);
            await deleteCampaigns.ExecuteNonQueryAsync();
        }
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }
        await _factory.DisposeAsync();
    }

    // ── fixture helpers ─────────────────────────────────────────────────────

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
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

    private static async Task InsertSceneAsync(NpgsqlConnection conn, Guid id, Guid sessionId, string tokensJson)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@id, @sessionId, 'Test Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"11111111-1111-1111-1111-111111111111","x":0,"y":0,"radius":10}]}',
                true)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        cmd.Parameters.AddWithValue("tokens", tokensJson);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, Guid? ownerUserId,
        bool controlledByDm, bool npcStatsVisibleToParty, int currentHp, int maxHp)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}', @currentHp, @maxHp, 0, 10, 0,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @controlledByDm,
                 @npcStatsVisibleToParty, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("currentHp", currentHp);
        cmd.Parameters.AddWithValue("maxHp", maxHp);
        cmd.Parameters.AddWithValue("controlledByDm", controlledByDm);
        cmd.Parameters.AddWithValue("npcStatsVisibleToParty", npcStatsVisibleToParty);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"scenefog-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"SceneFog {label}",
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

    private HubConnection BuildConnection(string? token) =>
        new HubConnectionBuilder()
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

    private record AuthResponse(string Token, Guid UserId);
    private record TokenResponse(Guid Id, string EntityType, Guid EntityId, double X, double Y, int? CurrentHp, int? MaxHp);
    private record FogAreaResponse(Guid Id, double X, double Y, double Radius);
    private record SceneResponse(Guid Id, Guid SessionId, string Name, string Grid, Guid? MapAssetId, string? ImageUrl, bool IsActive, List<TokenResponse> Tokens, List<FogAreaResponse> RevealedAreas);
    private record FogStateResponse(Guid SceneId, List<FogAreaResponse> RevealedAreas);

    // ── projection: DM ─────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_sees_all_tokens_regardless_of_fog()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        Assert.Equal(4, scene!.Tokens.Count);
        Assert.Contains(scene.Tokens, t => t.Id == _hiddenTokenId); // อยู่นอกหมอกแต่ DM ต้องเห็น
    }

    [Fact]
    public async Task Dm_sees_real_hp_for_hidden_npc()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        var npcToken = scene!.Tokens.Single(t => t.Id == _hiddenNpcTokenId);
        Assert.Equal(30, npcToken.CurrentHp);
        Assert.Equal(59, npcToken.MaxHp);
    }

    // ── projection: Player ───────────────────────────────────────────────

    [Fact]
    public async Task Player_sees_only_tokens_within_revealed_fog_area()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        Assert.Contains(scene!.Tokens, t => t.Id == _revealedTokenId);
        Assert.DoesNotContain(scene.Tokens, t => t.Id == _hiddenTokenId);
    }

    [Fact]
    public async Task Player_response_raw_json_contains_no_trace_of_hidden_token()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var raw = await response.Content.ReadAsStringAsync();

        // adversarial ตามที่สั่ง — ยิงตรง ๆ ด้วย token ผู้เล่น ตรวจ raw string ไม่ใช่ deserialize
        // แล้วเช็ค field (กัน serializer เผลอ include อะไรที่โมเดล C# ไม่ได้ตั้งใจ)
        Assert.DoesNotContain(_hiddenTokenId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999", raw);
    }

    /// <summary>
    /// เคสขอบ — token ที่ระยะห่างจากศูนย์กลางวงหมอก**เท่ากับ**รัศมีเป๊ะ (ไม่ใช่แค่น้อยกว่า) ยืนยัน
    /// ว่า <c>&lt;=</c> (inclusive) เป็นพฤติกรรมที่ตั้งใจจริง ไม่ใช่ off-by-one โดยไม่รู้ตัว — ตัวเลือก
    /// ทางเลือก (exclusive) จะทำให้ token ที่อยู่ "ขอบพอดี" ของพื้นที่เปิดหายไปอย่างไม่เป็นธรรมชาติ
    /// (ผู้เล่นควรเห็นสิ่งที่อยู่ตรงขอบเขตที่ DM เปิดให้พอดี ไม่ใช่ให้หายไปเพราะ floating point เป๊ะ ๆ)
    /// </summary>
    [Fact]
    public async Task Token_exactly_at_fog_boundary_distance_is_visible()
    {
        // fixture: วงหมอกอยู่ที่ (0,0) รัศมี 10 — วาง token ที่ (10,0) ระยะห่างเท่ากับรัศมีเป๊ะ
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Character", EntityId = _playerCharacterId, X = 10.0, Y = 0.0 });
        createResponse.EnsureSuccessStatusCode();

        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        Assert.Contains(scene!.Tokens, t => Math.Abs(t.X - 10.0) < 0.001 && Math.Abs(t.Y) < 0.001);
    }

    [Fact]
    public async Task Player_sees_real_hp_for_own_party_character()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        var token = scene!.Tokens.Single(t => t.Id == _revealedTokenId);
        Assert.Equal(15, token.CurrentHp);
        Assert.Equal(20, token.MaxHp);
    }

    [Fact]
    public async Task Player_does_not_see_hp_of_hidden_npc()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var raw = await response.Content.ReadAsStringAsync();
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        var npcToken = scene!.Tokens.SingleOrDefault(t => t.Id == _hiddenNpcTokenId);
        Assert.NotNull(npcToken); // token เองยังเห็น (อยู่ในหมอกที่เปิด) แค่ HP ถูกซ่อน
        Assert.Null(npcToken!.CurrentHp);
        Assert.Null(npcToken.MaxHp);
        // ป้องกัน serializer เผลอ include ตัวเลข HP จริงเป็น string ที่อื่นในก้อน JSON
        Assert.DoesNotContain("\"30\"", raw);
    }

    [Fact]
    public async Task Player_sees_hp_of_npc_that_dm_made_visible()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        var npcToken = scene!.Tokens.Single(t => t.Id == _visibleNpcTokenId);
        Assert.Equal(10, npcToken.CurrentHp);
    }

    [Fact]
    public async Task Outsider_gets_404()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── fog reveal/hide ──────────────────────────────────────────────────

    [Fact]
    public async Task Dm_reveals_new_fog_area()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/fog/reveal", new { X = 100.0, Y = 100.0, Radius = 5.0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var area = await response.Content.ReadFromJsonAsync<FogAreaResponse>(JsonOptions);
        Assert.Equal(100.0, area!.X);

        // token ที่ (999,999) ยังนอกวงใหม่นี้อยู่ดี แต่ยืนยันว่าพื้นที่ถูกเพิ่มจริงผ่าน fog/hide
        var hideResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/fog/hide", new { AreaId = area.Id });
        var state = await hideResponse.Content.ReadFromJsonAsync<FogStateResponse>(JsonOptions);
        Assert.DoesNotContain(state!.RevealedAreas, a => a.Id == area.Id);
    }

    [Fact]
    public async Task Player_revealing_fog_gets_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/fog/reveal", new { X = 1.0, Y = 1.0, Radius = 5.0 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reveal_with_non_positive_radius_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/fog/reveal", new { X = 1.0, Y = 1.0, Radius = 0.0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hide_nonexistent_area_is_a_no_op_not_error()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/fog/hide", new { AreaId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── hub role-groups ──────────────────────────────────────────────────

    [Fact]
    public async Task Fog_changed_broadcasts_to_both_dm_and_player_role_groups()
    {
        await using var dmConnection = BuildConnection(_dmToken);
        await using var playerConnection = BuildConnection(_playerToken);

        var dmReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playerReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        dmConnection.On<JsonElement>("FogChanged", payload => dmReceived.TrySetResult(payload));
        playerConnection.On<JsonElement>("FogChanged", payload => playerReceived.TrySetResult(payload));

        await dmConnection.StartAsync();
        await dmConnection.InvokeAsync("JoinCampaign", _campaignId);
        await playerConnection.StartAsync();
        await playerConnection.InvokeAsync("JoinCampaign", _campaignId);

        await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/fog/reveal", new { X = 50.0, Y = 50.0, Radius = 3.0 });

        var dmCompleted = await Task.WhenAny(dmReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        var playerCompleted = await Task.WhenAny(playerReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(dmReceived.Task, dmCompleted);
        Assert.Same(playerReceived.Task, playerCompleted);
    }
}

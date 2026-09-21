using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StackExchange.Redis;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 5 — token บนแผนที่: ephemeral (ghost ผ่าน hub) + committed (PATCH) — design doc §14
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// POST   /campaigns/{cid}/scenes/{scid}/tokens              body { EntityType, EntityId, X, Y }
///        201 + TokenResponse · 400 = EntityType ไม่รู้จัก · 404 = entity ไม่อยู่แคมเปญนี้
///        403 = ผู้เล่นสร้าง token ให้ตัวละครคนอื่น/ผู้เล่นสร้าง token มอนสเตอร์
///
/// PATCH  /campaigns/{cid}/scenes/{scid}/tokens/{tid}         body { X, Y }
///        200 + TokenResponse · เขียน Redis ก่อนเสมอ (fast path) — Redis ใช้ไม่ได้ fallback เขียน
///        Postgres ตรง ๆ ทันที (ห้ามข้อมูลหาย) · 403 = ผู้เล่นย้าย token ที่ไม่ใช่ของตัวเอง
///
/// DELETE /campaigns/{cid}/scenes/{scid}/tokens/{tid}         204 · ล้าง pending ของ token นั้นด้วย
///
/// GET    /campaigns/{cid}/sessions/{sid}/scene (Part 4)      ต้องคืนตำแหน่งที่รวม pending ล่าสุด
///        แล้ว (novice check: "ลาก token แล้วตำแหน่งรอดหลัง refresh หน้า")
///
/// Hub    MoveTokenGhost(campaignId, sceneId, tokenId, x, y)  broadcast "TokenGhostMoved" ให้คนอื่น
///        ใน group — **ไม่แตะ DB/Redis เลย**
/// </summary>
public class SceneTokenEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "scene-token-endpoints-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _characterId = Guid.NewGuid();     // เจ้าของ = _playerUserId
    private readonly Guid _otherCharacterId = Guid.NewGuid(); // เจ้าของ = _otherPlayerUserId
    private readonly Guid _monsterId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _otherPlayerUserId;
    private string _otherPlayerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_otherPlayerUserId, _otherPlayerToken) = await RegisterAsync("other-player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Token Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignId, _otherPlayerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSceneAsync(conn, _sceneId, _sessionId);
        await InsertCharacterAsync(conn, _characterId, _campaignId, "Hero", _playerUserId);
        await InsertCharacterAsync(conn, _otherCharacterId, _campaignId, "Other Hero", _otherPlayerUserId);
        await InsertMonsterAsync(conn, _monsterId, _campaignId, "Goblin");
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
            deleteUsers.Parameters.AddWithValue(
                "ids", new[] { _dmUserId, _playerUserId, _otherPlayerUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }

        // ล้าง Redis pending ที่อาจค้างจากเทสต์นี้ (คนละ scene id ทุกรัน กัน false-positive ข้ามเทสต์
        // แต่ล้างไว้กันเปลืองหน่วยความจำ Redis สะสมยาว ๆ ระหว่างรันเทสต์ทั้งไฟล์)
        var store = _factory.Services.GetRequiredService<ISceneTokenStore>();
        await store.ClearAllPendingAsync(_sceneId);

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

    private static async Task InsertSceneAsync(NpgsqlConnection conn, Guid id, Guid sessionId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@id, @sessionId, 'Test Scene', '{}', '[]', '{}', true)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, Guid ownerUserId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}', 20, 20, 0, 10, 0,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", ownerUserId);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertMonsterAsync(NpgsqlConnection conn, Guid id, Guid campaignId, string name)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status)
            VALUES (@id, @campaignId, @name, 15, 7, '[]', '[]', 'Approved')
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"scenetoken-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"SceneToken {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token) => AuthedClient(_factory, token);

    private static HttpClient AuthedClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>เหมือน CreateDeadRedisFactory ใน EncounterEndpointsTests เป๊ะ — ชี้ Redis ไปพอร์ตที่
    /// ไม่มีจริงเพื่อพิสูจน์ fallback ทาง Postgres โดยไม่ต้องปิด Redis จริงที่เทสต์อื่นใช้ร่วมกันอยู่</summary>
    private WebApplicationFactory<Program> CreateDeadRedisFactory() =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
                    "localhost:6399,abortConnect=false,connectTimeout=200,connectRetry=0,syncTimeout=200"));
            }));

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

    private async Task<Guid> CreateCharacterTokenAsync(string token, Guid characterId, double x = 1, double y = 1)
    {
        var response = await AuthedClient(token).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Character", EntityId = characterId, X = x, Y = y });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        return body!.Id;
    }

    private static async Task<string> GetTokensJsonAsync(NpgsqlConnection conn, Guid sceneId)
    {
        await using var cmd = new NpgsqlCommand("SELECT tokens::text FROM scenes WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", sceneId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// ตำแหน่งของ token ตัวหนึ่งที่เก็บอยู่ใน Postgres จริง — null = ไม่มี token นั้นในแถว
    ///
    /// **ทำไมต้อง parse ไม่ใช่ค้นสตริง** (แก้ 2026-09-21): เทสต์กลุ่มนี้เคยยืนยันว่า flush แล้ว/ยังไม่ flush
    /// ด้วย `Assert.DoesNotContain("99", tokensJson)` ซึ่งไปแมตช์เลขที่อยู่กลาง **GUID** ได้
    /// (เคสจริงที่ทำให้ตก: entityId `…b5f5e85699f5` มี "99" อยู่ข้างใน) — สุ่มพังตาม GUID ที่ได้ในรอบนั้น
    /// ฝั่ง `Assert.Contains` อันตรายกว่าอีกเพราะผ่านแบบ vacuous ได้ (เลขไปตรงกับ GUID ทั้งที่ยังไม่ได้เขียนจริง)
    /// · เจตนาของเทสต์คือ "พิกัดของ token ตัวนี้ในฐานเป็นเท่าไร" จึงต้องอ่านค่าจริงจาก JSON ไม่ใช่ค้นตัวเลขลอย ๆ
    /// </summary>
    private static async Task<(double X, double Y)?> GetStoredPositionAsync(
        NpgsqlConnection conn, Guid sceneId, Guid tokenId)
    {
        var json = await GetTokensJsonAsync(conn, sceneId);
        using var doc = JsonDocument.Parse(json);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.GetProperty("id").GetGuid() == tokenId)
            {
                return (element.GetProperty("x").GetDouble(), element.GetProperty("y").GetDouble());
            }
        }

        return null;
    }

    private record AuthResponse(string Token, Guid UserId);
    private record TokenResponse(Guid Id, string EntityType, Guid EntityId, double X, double Y);
    private record TokenErrorResponse(string Reason);
    private record SceneResponse(Guid Id, Guid SessionId, string Name, string Grid, Guid? MapAssetId, string? ImageUrl, bool IsActive, List<TokenResponse> Tokens);

    // ── POST create ───────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_creates_character_token()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Character", EntityId = _characterId, X = 5.0, Y = 5.0 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.Equal("Character", body!.EntityType);
        Assert.Equal(_characterId, body.EntityId);
    }

    [Fact]
    public async Task Player_creates_token_for_own_character()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Character", EntityId = _characterId, X = 1.0, Y = 1.0 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Player_creating_token_for_someone_elses_character_gets_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Character", EntityId = _otherCharacterId, X = 1.0, Y = 1.0 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dm_creates_monster_token()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Monster", EntityId = _monsterId, X = 2.0, Y = 2.0 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Player_creating_monster_token_gets_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Monster", EntityId = _monsterId, X = 2.0, Y = 2.0 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_invalid_entity_type_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Vehicle", EntityId = _characterId, X = 1.0, Y = 1.0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TokenErrorResponse>(JsonOptions);
        Assert.Equal("invalid_entity_type", body!.Reason);
    }

    [Fact]
    public async Task Create_referencing_nonexistent_character_returns_404()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Character", EntityId = Guid.NewGuid(), X = 1.0, Y = 1.0 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH move ────────────────────────────────────────────────────────

    [Fact]
    public async Task Move_updates_pending_and_get_scene_reflects_new_position_immediately()
    {
        var tokenId = await CreateCharacterTokenAsync(_dmToken, _characterId, 1, 1);

        var move = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}", new { X = 99.0, Y = 88.0 });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);

        // novice check: GET scene ต้องเห็นตำแหน่งใหม่ทันที แม้ยังไม่ flush เข้า Postgres
        // (พิสูจน์ทางอ้อมว่ายังไม่ flush: อ่าน Postgres ตรง ๆ ต้องยังเป็นตำแหน่งเก่า)
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        var storedBeforeFlush = await GetStoredPositionAsync(conn, _sceneId, tokenId);
        Assert.Equal((1.0, 1.0), storedBeforeFlush);

        var sceneResponse = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var scene = await sceneResponse.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);
        var token = scene!.Tokens.Single(t => t.Id == tokenId);
        Assert.Equal(99.0, token.X);
        Assert.Equal(88.0, token.Y);
    }

    [Fact]
    public async Task Move_falls_back_to_postgres_directly_when_redis_is_unavailable()
    {
        var tokenId = await CreateCharacterTokenAsync(_dmToken, _characterId, 1, 1);

        using var deadRedisFactory = CreateDeadRedisFactory();
        var response = await AuthedClient(deadRedisFactory, _dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}", new { X = 42.0, Y = 24.0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ต้องเขียนลง Postgres ตรง ๆ ทันที (ไม่ใช่แค่ตอบ 200 แล้วข้อมูลหาย)
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        Assert.Equal((42.0, 24.0), await GetStoredPositionAsync(conn, _sceneId, tokenId));
    }

    [Fact]
    public async Task Player_moving_own_character_token_succeeds()
    {
        var tokenId = await CreateCharacterTokenAsync(_playerToken, _characterId);
        var response = await AuthedClient(_playerToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}", new { X = 3.0, Y = 3.0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Player_moving_someone_elses_character_token_gets_403()
    {
        var tokenId = await CreateCharacterTokenAsync(_dmToken, _otherCharacterId);
        var response = await AuthedClient(_playerToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}", new { X = 3.0, Y = 3.0 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dm_can_move_any_character_token()
    {
        var tokenId = await CreateCharacterTokenAsync(_dmToken, _characterId);
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}", new { X = 7.0, Y = 7.0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Player_moving_monster_token_gets_403()
    {
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens",
            new { EntityType = "Monster", EntityId = _monsterId, X = 1.0, Y = 1.0 });
        var created = await createResponse.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);

        var response = await AuthedClient(_playerToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{created!.Id}", new { X = 3.0, Y = 3.0 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── DELETE ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_removes_token_and_clears_pending()
    {
        var tokenId = await CreateCharacterTokenAsync(_dmToken, _characterId);
        await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}", new { X = 50.0, Y = 50.0 });

        var deleteResponse = await AuthedClient(_dmToken).DeleteAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var flusher = _factory.Services.GetRequiredService<SceneTokenFlusher>();
        await flusher.FlushAsync(_sceneId);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        // ลบแล้วต้องไม่มี token ตัวนี้เหลือในฐานเลย — ถ้า pending เก่าฟื้นคืนชีพตอน flush จะโผล่กลับมาที่ (50,50)
        Assert.Null(await GetStoredPositionAsync(conn, _sceneId, tokenId));
        Assert.DoesNotContain(tokenId.ToString(), await GetTokensJsonAsync(conn, _sceneId));
    }

    // ── flush ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Flush_merges_pending_into_postgres_and_clears_redis()
    {
        var tokenId = await CreateCharacterTokenAsync(_dmToken, _characterId, 1, 1);
        await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/tokens/{tokenId}", new { X = 77.0, Y = 66.0 });

        var flusher = _factory.Services.GetRequiredService<SceneTokenFlusher>();
        await flusher.FlushAsync(_sceneId);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        Assert.Equal((77.0, 66.0), await GetStoredPositionAsync(conn, _sceneId, tokenId));

        var store = _factory.Services.GetRequiredService<ISceneTokenStore>();
        var pending = await store.GetPendingPositionsAsync(_sceneId);
        Assert.Empty(pending);
    }

    // ── hub ghost move ────────────────────────────────────────────────────

    [Fact]
    public async Task Ghost_move_broadcasts_to_others_without_touching_db()
    {
        await using var senderConnection = BuildConnection(_dmToken);
        await using var receiverConnection = BuildConnection(_playerToken);

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverConnection.On<JsonElement>("TokenGhostMoved", payload => received.TrySetResult(payload));

        await senderConnection.StartAsync();
        await senderConnection.InvokeAsync("JoinCampaign", _campaignId);
        await receiverConnection.StartAsync();
        await receiverConnection.InvokeAsync("JoinCampaign", _campaignId);

        var tokenId = Guid.NewGuid();
        await senderConnection.InvokeAsync("MoveTokenGhost", _campaignId, _sceneId, tokenId, 123.0, 456.0);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);
        var payload = await received.Task;
        Assert.Equal(tokenId, payload.GetProperty("tokenId").GetGuid());
        Assert.Equal(123.0, payload.GetProperty("x").GetDouble());

        // ต้องไม่แตะ Postgres/Redis เลย — token ที่อ้างถึงไม่มีอยู่จริงด้วยซ้ำ (สุ่ม guid ใหม่) ถ้า
        // ghost move พยายามเขียนอะไรจริงจะพังตรงนี้ ไม่ใช่แค่เงียบ ๆ ผิด
        var store = _factory.Services.GetRequiredService<ISceneTokenStore>();
        var pending = await store.GetPendingPositionsAsync(_sceneId);
        Assert.DoesNotContain(tokenId, pending.Keys);
    }

    [Fact]
    public async Task Ghost_move_from_connection_not_in_campaign_group_throws()
    {
        await using var connection = BuildConnection(_outsiderToken);
        await connection.StartAsync();
        // ไม่เรียก JoinCampaign เลย

        await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("MoveTokenGhost", _campaignId, _sceneId, Guid.NewGuid(), 1.0, 1.0));
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 6 — พื้นเดินยาก/กำแพงไปถึงจอผู้เล่นผ่าน GET scene **กรองตามหมอกที่ server**
///
/// โจทย์ของ Part นี้: "ฝั่งผู้เล่นแสดงเฉพาะในพื้นที่ที่เปิดหมอกแล้ว" — กรองที่ server เท่านั้น ไม่ส่งทั้งก้อน
/// ไปให้ client ซ่อนเอง (design doc §13.3 / §1.2) · กติกา "มองเห็นไหม" ใช้ตัวเดียวกับ token
/// (FogLayerSerialization.IsVisibleTo) ไม่เขียนสูตรวงกลมใหม่
///
/// ฉาก: หมอกเปิดวง (0,0) รัศมี 5 · พื้นที่เห็นได้ = พื้นเดินยาก (1,1) + กำแพง (2,0)
/// · พื้นในหมอก = พื้นเดินยาก (777,777) + กำแพง (888,888)
/// </summary>
public class SceneTerrainProjectionTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn,
            """
            INSERT INTO campaigns (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@c, @dm, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now());
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (gen_random_uuid(), @c, @dm, 'Dm', now()), (gen_random_uuid(), @c, @player, 'Player', now());
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@s, @c, 'Online', 'Active', NULL, now(), NULL);
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, terrain, is_active)
            VALUES (@sc, @s, 'Projection Scene', '{}', '[]',
                '{"revealedAreas":[{"id":"77777777-7777-7777-7777-777777777777","x":0,"y":0,"radius":5}]}',
                '{"difficult":[[1,1],[777,777]],"blocked":[[2,0],[888,888]]}',
                true);
            """,
            ("c", _campaignId), ("dm", _dmUserId), ("player", _playerUserId), ("s", _sessionId),
            ("sc", _sceneId), ("name", $"Terrain Projection {_suffix}"));
    }

    [Fact]
    public async Task Dm_scene_includes_all_terrain_including_squares_inside_the_fog()
    {
        var scene = await GetSceneAsync(_dmToken);

        Assert.Equal([(1, 1), (777, 777)], scene.Terrain.Difficult.Select(s => (s.X, s.Y)).ToArray());
        Assert.Equal([(2, 0), (888, 888)], scene.Terrain.Blocked.Select(s => (s.X, s.Y)).ToArray());
    }

    /// <summary>ผู้เล่นเห็นหนองน้ำ/กำแพงเฉพาะในพื้นที่ที่เปิดหมอกแล้ว — ตรงกับที่ token ถูกกรอง</summary>
    [Fact]
    public async Task Player_scene_includes_only_terrain_inside_revealed_fog()
    {
        var scene = await GetSceneAsync(_playerToken);

        Assert.Equal([(1, 1)], scene.Terrain.Difficult.Select(s => (s.X, s.Y)).ToArray());
        Assert.Equal([(2, 0)], scene.Terrain.Blocked.Select(s => (s.X, s.Y)).ToArray());
    }

    /// <summary>
    /// adversarial — ตรวจ raw JSON ไม่ใช่แค่ deserialize แล้วดู field ที่นึกออก · ค้นรูปแบบ <c>"x":777</c>
    /// เป๊ะ ๆ ไม่ใช่เลข 777 ลอย ๆ — เลขลอยชนกับ GUID สุ่มได้ (บทเรียนจากเทสต์หมอกที่ flaky ด้วยเหตุนี้)
    /// </summary>
    [Fact]
    public async Task Player_raw_json_contains_no_trace_of_terrain_inside_the_fog()
    {
        var response = await Client(_playerToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"x\":1", raw);
        Assert.DoesNotContain("\"x\":777", raw);
        Assert.DoesNotContain("\"x\":888", raw);
    }

    [Fact]
    public async Task Scene_without_terrain_returns_empty_lists_not_null()
    {
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, "UPDATE scenes SET terrain = '{}'::jsonb WHERE id = @id", ("id", _sceneId));
        }

        var scene = await GetSceneAsync(_playerToken);

        Assert.NotNull(scene.Terrain);
        Assert.Empty(scene.Terrain.Difficult);
        Assert.Empty(scene.Terrain.Blocked);
    }

    /// <summary>
    /// DM ระบายพื้น → ทุก role ต้องได้สัญญาณให้ดึงแผนที่ใหม่ (จอผู้เล่นเห็นหนองน้ำและ reachable หดทันที)
    /// · **payload ต้องไม่มีรายการช่องเลย** — ต่างจาก FogChanged ที่ส่งวงหมอกได้เพราะไม่ใช่ความลับ พื้นในหมอก
    /// เป็นความลับ ผู้เล่นต้องได้เฉพาะส่วนที่ server กรองแล้วผ่าน GET scene เท่านั้น
    /// </summary>
    [Fact]
    public async Task Painting_terrain_broadcasts_a_content_free_change_signal_to_both_roles()
    {
        await using var dmConnection = BuildConnection(_dmToken);
        await using var playerConnection = BuildConnection(_playerToken);

        var dmReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playerReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        dmConnection.On<JsonElement>("TerrainChanged", payload => dmReceived.TrySetResult(payload));
        playerConnection.On<JsonElement>("TerrainChanged", payload => playerReceived.TrySetResult(payload));

        await dmConnection.StartAsync();
        await dmConnection.InvokeAsync("JoinCampaign", _campaignId);
        await playerConnection.StartAsync();
        await playerConnection.InvokeAsync("JoinCampaign", _campaignId);

        var patch = await Client(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/terrain",
            new { Paint = "Difficult", Squares = new[] { new { X = 950, Y = 950 } } });
        patch.EnsureSuccessStatusCode();

        Assert.Same(dmReceived.Task, await Task.WhenAny(dmReceived.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Same(playerReceived.Task, await Task.WhenAny(playerReceived.Task, Task.Delay(TimeSpan.FromSeconds(10))));

        var payload = await playerReceived.Task;
        Assert.Equal(_sceneId, payload.GetProperty("sceneId").GetGuid());
        Assert.False(payload.TryGetProperty("difficult", out _), "สัญญาณต้องไม่พกรายการพื้นเดินยาก");
        Assert.False(payload.TryGetProperty("blocked", out _), "สัญญาณต้องไม่พกรายการกำแพง");
        Assert.DoesNotContain("950", payload.GetRawText());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<SceneResponse> GetSceneAsync(string token)
    {
        var response = await Client(token).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions))!;
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/campaign"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = _factory.Server.CreateWebSocketClient();
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"terrain-projection-{label}-{_suffix}@example.test",
            Password = "terrain-projection-secret-1",
            DisplayName = $"Terrain Projection {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = @c", ("c", _campaignId));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", new[] { _dmUserId, _playerUserId }));
        _factory.Dispose();
    }

    private record AuthTokenResponse(string Token, Guid UserId);

    private record TerrainSquareResponse(int X, int Y);

    private record TerrainResponse(List<TerrainSquareResponse> Difficult, List<TerrainSquareResponse> Blocked);

    private record SceneResponse(Guid Id, TerrainResponse Terrain);
}

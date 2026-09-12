using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 5 — DoD: พื้นเดินยาก/กำแพงของฉาก **มีผลกับการเดินผ่าน HTTP จริง** (reachable /
/// preview / commit) ไม่ใช่แค่ใน engine · ก่อน Part นี้ทั้งสาม endpoint ส่ง <c>TerrainField.Empty</c>
/// ผู้เล่นจึงเดินตรงได้เสมอ และ Dijkstra ของ Part 8 ไม่เคยได้ทำงานจริง
///
/// เคสระดับ engine มีอยู่แล้วใน MovementReachabilityTests — ไฟล์นี้ยืนยันว่าข้อมูลจาก `scenes.terrain`
/// ไปถึง engine จริง · ตั้งพื้นด้วย SQL ตรง ๆ (คอลัมน์คือสัญญา) เพื่อไม่ให้เทสต์การเดินพึ่ง endpoint
/// ระบายพื้น — endpoint นั้นมีเทสต์ของตัวเองใน SceneTerrainEndpointsTests
///
/// ฉาก: ฮีโร่ที่ (0,0) speed 30 ฟุต = 6 ช่อง (ทแยงเท่าตรง ตาม SRD) · พื้นปกติ 5 ฟุต/ช่อง
/// พื้นเดินยาก 10 ฟุต/ช่อง (SRD 5.2: *"Every foot of movement in Difficult Terrain costs 1 extra foot"*)
/// </summary>
public class MovementTerrainEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _heroTokenId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private Guid _playerUserId;
    private string _playerToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var tokens = $$"""
            [{"id":"{{_heroTokenId}}","entityType":"Character","entityId":"{{_heroId}}","x":0,"y":0}]
            """;

        await ExecAsync(conn,
            """
            INSERT INTO campaigns (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@c, @dm, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now());
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (gen_random_uuid(), @c, @dm, 'Dm', now()), (gen_random_uuid(), @c, @player, 'Player', now());
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@s, @c, 'Online', 'Active', NULL, now(), NULL);
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES (@hero, @c, @player, 'ฮีโร่', '{}'::jsonb, 20, 20, 0, 12, 0, 30,
                    ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, false, now());
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@sc, @s, 'Terrain Walk Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"66666666-6666-6666-6666-666666666666","x":0,"y":0,"radius":40}]}', true);
            """,
            ("c", _campaignId), ("dm", _dmUserId), ("player", _playerUserId), ("s", _sessionId),
            ("hero", _heroId), ("sc", _sceneId), ("tokens", tokens), ("name", $"Terrain Walk {_suffix}"));
    }

    // ── พื้นเดินยาก ──────────────────────────────────────────────────────────

    /// <summary>
    /// พื้นโล่งไปได้ถึง x=6 (169 ช่อง ดู MovementReachableEndpointsTests) · วางแถบพื้นเดินยาก x=1..6
    /// y=-6..6 ขวางฝั่งตะวันออก → ทุกก้าวในแถบคิด 10 ฟุต ไปได้แค่ x=3 (10+10+10 = 30) และอ้อมแถบก็ไม่ได้
    /// (ต้องไป y=7 ซึ่งไกล 7 ช่อง) · **ขอบเขตต้องแคบลงจริง ไม่ใช่วงกลมเท่าเดิม**
    /// </summary>
    [Fact]
    public async Task Difficult_terrain_really_shrinks_the_reachable_area()
    {
        var band = new List<(int, int)>();
        for (var x = 1; x <= 6; x++)
        for (var y = -6; y <= 6; y++)
            band.Add((x, y));
        await SetTerrainAsync(difficult: band);

        var body = await ReachableAsync();

        Assert.True(body.Squares.Count < 169, $"ขอบเขตต้องแคบกว่าพื้นโล่ง (169) แต่ได้ {body.Squares.Count}");
        Assert.Contains(body.Squares, s => s.X == 3 && s.Y == 0 && s.CostFeet == 30);
        Assert.DoesNotContain(body.Squares, s => s.X >= 4);
    }

    /// <summary>
    /// **หัวใจของ DoD — อ้อมต้องถูกกว่าลุยตรง** · แถบพื้นเดินยาก (1,0) (2,0) (3,0) ขวางทางตรงไป (4,0):
    ///   - ลุยตรง 3 ช่องยาก + 1 ช่องปกติ = 10+10+10+5 = **35 ฟุต** (เกินงบ 30) — ถามราคาจาก preview จริง
    ///   - อ้อมทแยงขึ้น (1,1)(2,1)(3,1) แล้วลง (4,0) = 4 ก้าวปกติ = **20 ฟุต**
    /// reachable ต้องให้ (4,0) ที่ 20 ฟุต และเส้นทางที่แนบมาต้องไม่เหยียบแถบเลย
    /// </summary>
    [Fact]
    public async Task Detour_around_difficult_terrain_is_cheaper_than_wading_straight_through()
    {
        await SetTerrainAsync(difficult: [(1, 0), (2, 0), (3, 0)]);

        var straight = await PreviewAsync((0, 0), (1, 0), (2, 0), (3, 0), (4, 0));
        Assert.Equal(HttpStatusCode.OK, straight.StatusCode);
        var straightCost = (await straight.Content.ReadFromJsonAsync<PreviewResponse>(JsonOptions))!;
        Assert.Equal(35, straightCost.CostFeet);
        Assert.False(straightCost.IsAffordable);

        var target = Assert.Single((await ReachableAsync()).Squares.Where(s => s.X == 4 && s.Y == 0));
        Assert.Equal(20, target.CostFeet);
        Assert.True(target.CostFeet < straightCost.CostFeet);
        Assert.DoesNotContain(target.Path, p => p.Y == 0 && p.X is >= 1 and <= 3);
    }

    /// <summary>
    /// commit ต้องอ่านพื้นชุดเดียวกับ reachable — สองขั้นในเทสต์เดียว:
    ///   1. ส่งทางตรงลุยแถบพื้นเดินยากไป commit เอง (35 ฟุต เกินงบ 30) → ต้องโดนปฏิเสธ token ไม่ขยับ
    ///      (ถ้า commit ไม่อ่านพื้น ทางนี้คิดแค่ 20 ฟุตแล้วผ่าน — ผู้เล่นลัดพื้นเดินยากได้ด้วยการส่งเส้นทางเอง)
    ///   2. ส่งเส้นทางอ้อมที่ reachable คืนมา → ต้องผ่านและ token ไปอยู่ปลายทาง
    ///      (ถ้าสองฝั่งอ่านพื้นคนละชุด ผู้เล่นจะลากไปวางในช่องที่ไฮไลต์ว่าไปได้แล้วโดนปฏิเสธ)
    ///
    /// หมายเหตุ owner: เวอร์ชันแรกของเทสต์นี้มีแค่ขั้นที่ 2 ซึ่ง **ผ่านตั้งแต่ตอนยังเป็น stub** (ไม่มีพื้น
    /// reachable ก็คืนทางตรง 20 ฟุตที่ commit รับอยู่แล้ว) จึงพิสูจน์อะไรไม่ได้ — เพิ่มขั้นที่ 1 ให้แดงจริงก่อน
    /// </summary>
    [Fact]
    public async Task Commit_reads_the_same_terrain_as_reachable()
    {
        await SetTerrainAsync(difficult: [(1, 0), (2, 0), (3, 0)]);

        var wading = await CommitAsync((0, 0), (1, 0), (2, 0), (3, 0), (4, 0));
        Assert.Equal(HttpStatusCode.BadRequest, wading.StatusCode);
        Assert.Equal((0, 0), await TokenPositionAsync());

        var target = (await ReachableAsync()).Squares.Single(s => s.X == 4 && s.Y == 0);
        var detour = await CommitAsync(target.Path.Select(p => (p.X, p.Y)).ToArray());

        detour.EnsureSuccessStatusCode();
        Assert.Equal((4, 0), await TokenPositionAsync());
    }

    // ── กำแพง ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Walls_are_never_in_the_reachable_area()
    {
        await SetTerrainAsync(blocked: [(1, -1), (1, 0), (1, 1)]);

        var body = await ReachableAsync();

        Assert.DoesNotContain(body.Squares, s => s.X == 1 && s.Y is >= -1 and <= 1);
    }

    [Fact]
    public async Task Preview_through_a_wall_is_rejected()
    {
        await SetTerrainAsync(blocked: [(1, -1), (1, 0), (1, 1)]);

        var response = await PreviewAsync((0, 0), (1, 0), (2, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>หัวใจของความปลอดภัยฝั่งการเดิน — ส่งเส้นทางทะลุกำแพงมาเองตรง ๆ ต้องโดนปฏิเสธ
    /// และ token ต้องไม่ขยับแม้แต่ช่องเดียว</summary>
    [Fact]
    public async Task Commit_through_a_wall_is_rejected_and_the_token_does_not_move()
    {
        await SetTerrainAsync(blocked: [(1, -1), (1, 0), (1, 1)]);

        var response = await CommitAsync((0, 0), (1, 0), (2, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0), await TokenPositionAsync());
    }

    /// <summary>
    /// กฎมุมกำแพง (SRD 5.2 "Corners" ที่ engine ทำไว้แล้ว) ต้องมีผลผ่าน HTTP: กำแพงที่ (1,0) ช่องเดียว
    ///   - ก้าวทแยง (0,0)→(1,1) เฉียดมุมกำแพง → preview ปฏิเสธ
    ///   - reachable จึงให้ (1,1) ที่ 10 ฟุต (ต้องอ้อม (0,1) ก่อน) ไม่ใช่ 5 ฟุตแบบทแยงตรง ๆ
    /// </summary>
    [Fact]
    public async Task Diagonal_steps_cannot_cut_a_wall_corner()
    {
        await SetTerrainAsync(blocked: [(1, 0)]);

        var cut = await PreviewAsync((0, 0), (1, 1));
        Assert.Equal(HttpStatusCode.BadRequest, cut.StatusCode);

        var body = await ReachableAsync();
        Assert.Contains(body.Squares, s => s.X == 1 && s.Y == 1 && s.CostFeet == 10);
        Assert.Contains(body.Squares, s => s.X == 1 && s.Y == -1 && s.CostFeet == 10);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task SetTerrainAsync(IEnumerable<(int X, int Y)>? difficult = null, IEnumerable<(int X, int Y)>? blocked = null)
    {
        static string Squares(IEnumerable<(int X, int Y)>? s) =>
            "[" + string.Join(",", (s ?? []).Select(p => $"[{p.X},{p.Y}]")) + "]";

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "UPDATE scenes SET terrain = @t::jsonb WHERE id = @id",
            ("t", $"{{\"difficult\":{Squares(difficult)},\"blocked\":{Squares(blocked)}}}"), ("id", _sceneId));
    }

    private async Task<ReachableResponse> ReachableAsync()
    {
        var response = await AuthedClient().GetAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/reachable/{_heroId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReachableResponse>(JsonOptions))!;
    }

    private Task<HttpResponseMessage> PreviewAsync(params (int X, int Y)[] path) =>
        AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/preview",
            new { CharacterId = _heroId, Path = path.Select(p => new { X = (double)p.X, Y = (double)p.Y }).ToArray(), Disengage = false });

    private Task<HttpResponseMessage> CommitAsync(params (int X, int Y)[] path) =>
        AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/commit",
            new { CharacterId = _heroId, Path = path.Select(p => new { X = (double)p.X, Y = (double)p.Y }).ToArray(), Disengage = false });

    private async Task<(int X, int Y)> TokenPositionAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT (t->>'x')::float, (t->>'y')::float
            FROM scenes s, jsonb_array_elements(s.tokens) t
            WHERE s.id = @sceneId AND (t->>'id')::uuid = @tokenId
            """, conn);
        cmd.Parameters.AddWithValue("sceneId", _sceneId);
        cmd.Parameters.AddWithValue("tokenId", _heroTokenId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ((int)Math.Round(reader.GetDouble(0)), (int)Math.Round(reader.GetDouble(1)));
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"terrain-walk-{label}-{_suffix}@example.test",
            Password = "terrain-walk-secret-1",
            DisplayName = $"Terrain Walk {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _playerToken);
        return client;
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

    private record PathPointResponse(int X, int Y);

    private record ReachableSquareResponse(int X, int Y, int CostFeet, List<PathPointResponse> Path);

    private record ReachableResponse(Guid CharacterId, int BudgetFeet, List<ReachableSquareResponse> Squares);

    private record PreviewResponse(Guid CharacterId, int CostFeet, bool IsAffordable);
}

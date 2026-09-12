using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 5 — PATCH/GET /campaigns/{cid}/scenes/{sceneId}/terrain (DM ระบายพื้นเดินยาก/กำแพง)
///
/// **เทสต์ความปลอดภัยเป็นตัวหลัก** — พื้น/กำแพงกำหนดว่าใครเดินไปไหนได้ ถ้าผู้เล่นเขียนได้ จะเปิด
/// กำแพงให้ตัวเองเดินทะลุ · และ RLS ของ `scenes` ให้สมาชิกทุกคน UPDATE ได้ที่ชั้น DB การกันจึงอยู่ที่
/// endpoint ที่เดียว
/// </summary>
public class SceneTerrainEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "scene-terrain-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();
    private readonly Guid _otherSceneId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _otherDmUserId;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_otherDmUserId, _) = await RegisterAsync("other-dm");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn,
            """
            INSERT INTO campaigns (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@c, @dm, @n1, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now()),
                   (@oc, @odm, @n2, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now());
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (gen_random_uuid(), @c, @dm, 'Dm', now()),
                   (gen_random_uuid(), @c, @player, 'Player', now()),
                   (gen_random_uuid(), @oc, @odm, 'Dm', now());
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@s, @c, 'Online', 'Active', NULL, now(), NULL),
                   (@os, @oc, 'Online', 'Active', NULL, now(), NULL);
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@sc, @s, 'Terrain Scene', '{}', '[]', '{}', true),
                   (@osc, @os, 'Other Scene', '{}', '[]', '{}', true);
            """,
            ("c", _campaignId), ("oc", _otherCampaignId), ("dm", _dmUserId), ("odm", _otherDmUserId),
            ("player", _playerUserId), ("s", _sessionId), ("os", _otherSessionId),
            ("sc", _sceneId), ("osc", _otherSceneId),
            ("n1", $"Terrain Campaign {_suffix}"), ("n2", $"Other Terrain Campaign {_suffix}"));
    }

    // ── DM ระบายได้ ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_paints_difficult_terrain_and_gets_it_back()
    {
        var response = await PatchAsync(_dmToken, _sceneId, "Difficult", (1, 0), (2, 0));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<TerrainResponse>(JsonOptions))!;
        Assert.Equal([(1, 0), (2, 0)], body.Difficult.Select(s => (s.X, s.Y)).ToArray());
        Assert.Empty(body.Blocked);
    }

    [Fact]
    public async Task Dm_painted_terrain_is_persisted_in_the_scene_row()
    {
        (await PatchAsync(_dmToken, _sceneId, "Blocked", (3, 3))).EnsureSuccessStatusCode();

        Assert.Equal("""{"blocked": [[3, 3]], "difficult": []}""", await RawTerrainAsync(_sceneId));
    }

    [Fact]
    public async Task Dm_can_read_terrain_back_with_get()
    {
        (await PatchAsync(_dmToken, _sceneId, "Blocked", (4, 1))).EnsureSuccessStatusCode();

        var response = await AuthedClient(_dmToken).GetAsync(TerrainUrl(_campaignId, _sceneId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<TerrainResponse>(JsonOptions))!;
        Assert.Equal([(4, 1)], body.Blocked.Select(s => (s.X, s.Y)).ToArray());
    }

    [Fact]
    public async Task Dm_clear_removes_painted_squares()
    {
        (await PatchAsync(_dmToken, _sceneId, "Blocked", (1, 1), (2, 2))).EnsureSuccessStatusCode();

        var response = await PatchAsync(_dmToken, _sceneId, "Clear", (1, 1));

        var body = (await response.Content.ReadFromJsonAsync<TerrainResponse>(JsonOptions))!;
        Assert.Equal([(2, 2)], body.Blocked.Select(s => (s.X, s.Y)).ToArray());
    }

    /// <summary>
    /// **เหตุผลที่แยกคอลัมน์ `terrain` ออกจาก `grid`** — PATCH grid สร้าง `grid` ใหม่ทั้งก้อน ถ้าพื้นอยู่ใน
    /// grid การจัดกริดครั้งเดียวจะลบกำแพงทั้งฉากทิ้ง · เทสต์นี้ล็อกไว้ไม่ให้ใครย้ายพื้นกลับเข้า grid
    /// </summary>
    [Fact]
    public async Task Recalibrating_the_grid_does_not_wipe_painted_terrain()
    {
        (await PatchAsync(_dmToken, _sceneId, "Blocked", (5, 5))).EnsureSuccessStatusCode();

        var grid = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/grid", new { PixelsPerSquare = 64.0, OffsetX = 0.0, OffsetY = 0.0 });
        grid.EnsureSuccessStatusCode();

        var body = (await (await AuthedClient(_dmToken).GetAsync(TerrainUrl(_campaignId, _sceneId)))
            .Content.ReadFromJsonAsync<TerrainResponse>(JsonOptions))!;
        Assert.Equal([(5, 5)], body.Blocked.Select(s => (s.X, s.Y)).ToArray());
    }

    // ── สิทธิ์: DM-only จริง ─────────────────────────────────────────────────

    /// <summary>หัวใจของความปลอดภัย — ผู้เล่นลบกำแพงให้ตัวเองเดินทะลุไม่ได้ และพื้นในฉากต้องไม่เปลี่ยน</summary>
    [Fact]
    public async Task Player_cannot_paint_terrain_and_the_scene_is_untouched()
    {
        (await PatchAsync(_dmToken, _sceneId, "Blocked", (1, 0))).EnsureSuccessStatusCode();
        var before = await RawTerrainAsync(_sceneId);

        var response = await PatchAsync(_playerToken, _sceneId, "Clear", (1, 0));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await RawTerrainAsync(_sceneId));
    }

    [Fact]
    public async Task Player_cannot_read_terrain()
    {
        var response = await AuthedClient(_playerToken).GetAsync(TerrainUrl(_campaignId, _sceneId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>เช็คสิทธิ์ก่อน validate — ผู้เล่นที่ส่งเนื้อหาผิดต้องได้ 403 ไม่ใช่ 400
    /// (ไม่เปิดให้คนไม่มีสิทธิ์ใช้ endpoint นี้ส่องกฎ validation)</summary>
    [Fact]
    public async Task Player_with_invalid_content_still_gets_403_not_400()
    {
        var response = await PatchAsync(_playerToken, _sceneId, "Lava", (1, 0));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_gets_404()
    {
        var response = await PatchAsync(_outsiderToken, _sceneId, "Blocked", (1, 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(TerrainUrl(_campaignId, _sceneId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>IDOR — DM ของแคมเปญนี้ส่ง sceneId ของแคมเปญอื่นมาใน route ของตัวเอง ต้องได้ 404
    /// และฉากของแคมเปญอื่นต้องไม่เปลี่ยน</summary>
    [Fact]
    public async Task Dm_cannot_paint_a_scene_of_another_campaign_through_their_own_campaign_route()
    {
        var response = await PatchAsync(_dmToken, _otherSceneId, "Blocked", (1, 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("{}", await RawTerrainAsync(_otherSceneId));
    }

    // ── validation + กันข้อมูลใหญ่ผิดปกติ ────────────────────────────────────

    [Theory]
    [InlineData("Lava")]
    [InlineData("difficult")]
    [InlineData("1")]
    [InlineData("")]
    public async Task Unknown_paint_is_rejected(string paint)
    {
        var response = await PatchAsync(_dmToken, _sceneId, paint, (1, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_or_empty_squares_are_rejected()
    {
        var missing = await AuthedClient(_dmToken).PatchAsJsonAsync(
            TerrainUrl(_campaignId, _sceneId), new { Paint = "Blocked" });
        var empty = await AuthedClient(_dmToken).PatchAsJsonAsync(
            TerrainUrl(_campaignId, _sceneId), new { Paint = "Blocked", Squares = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task More_squares_than_the_per_edit_cap_are_rejected()
    {
        var squares = Enumerable.Range(0, 1_001).Select(i => (i % 100, i / 100)).ToArray();

        var response = await PatchAsync(_dmToken, _sceneId, "Blocked", squares);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{}", await RawTerrainAsync(_sceneId));
    }

    [Fact]
    public async Task A_square_outside_the_coordinate_bounds_rejects_the_whole_edit()
    {
        var response = await PatchAsync(_dmToken, _sceneId, "Blocked", (1, 1), (1_001, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{}", await RawTerrainAsync(_sceneId));
    }

    /// <summary>พิกัด int.MinValue ต้องได้ 400 เหมือนพิกัดนอกขอบทั่วไป ไม่ใช่ 500 — ตัวเช็คขอบเดิมใช้
    /// <c>Math.Abs</c> ซึ่งโยน OverflowException กับค่านี้ (code review ของ Part นี้จับได้)</summary>
    [Fact]
    public async Task A_square_at_int_min_value_is_a_400_not_a_500()
    {
        var response = await PatchAsync(_dmToken, _sceneId, "Blocked", (1, 1), (int.MinValue, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{}", await RawTerrainAsync(_sceneId));
    }

    /// <summary>เพดานทั้งฉาก — ระบายทีละไม่เกินต่อครั้งแต่สะสมจนเกิน 10,000 ช่องต้องโดนปฏิเสธ</summary>
    [Fact]
    public async Task Painting_beyond_the_scene_cap_is_rejected()
    {
        for (var batch = 0; batch < 10; batch++)
        {
            var squares = Enumerable.Range(0, 1_000).Select(i => (i % 100, (batch * 10) + (i / 100))).ToArray();
            (await PatchAsync(_dmToken, _sceneId, "Difficult", squares)).EnsureSuccessStatusCode();
        }

        var response = await PatchAsync(_dmToken, _sceneId, "Blocked", (500, 500));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string TerrainUrl(Guid campaignId, Guid sceneId) => $"/campaigns/{campaignId}/scenes/{sceneId}/terrain";

    private Task<HttpResponseMessage> PatchAsync(string token, Guid sceneId, string paint, params (int X, int Y)[] squares) =>
        AuthedClient(token).PatchAsJsonAsync(
            TerrainUrl(_campaignId, sceneId),
            new { Paint = paint, Squares = squares.Select(s => new { s.X, s.Y }).ToArray() });

    private static async Task<string> RawTerrainAsync(Guid sceneId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT terrain::text FROM scenes WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", sceneId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"terrain-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Terrain {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)", ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId, _otherDmUserId, _outsiderUserId }));
        _factory.Dispose();
    }

    private record AuthTokenResponse(string Token, Guid UserId);

    private record TerrainSquareResponse(int X, int Y);

    private record TerrainResponse(List<TerrainSquareResponse> Difficult, List<TerrainSquareResponse> Blocked);
}

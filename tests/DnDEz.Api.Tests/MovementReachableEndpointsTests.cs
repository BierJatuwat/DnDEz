using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 10 — GET .../movement/reachable/{characterId}
/// "ลาก token แล้วต้องเห็นว่าไปได้ถึงไหน" — ขอบเขตที่คำนวณจากกฎจริง ไม่ใช่วงกลม
///
/// spatial-play-design §3.1 ระบุชัดว่าขอบเขตนี้เป็น **การกระจายตามต้นทุน ไม่ใช่วงรัศมี**
/// และเตือนไว้ด้วยว่าข้อความเดิมใน `player-side-design §4` ที่บอกให้วาด "วงรัศมี" นั้นใช้ไม่ได้
///
/// endpoint นี้คืน **เส้นทางที่ถูกที่สุดมาพร้อมทุกช่อง** ไม่ใช่แค่ราคา — เพราะตอนผู้เล่นปล่อย token
/// ลงช่องไหน client จะได้ส่งเส้นทางที่ server คำนวณเองกลับมา commit ได้เลย **client ไม่ต้องคิด
/// เส้นทางเอง** (ถ้าให้ client คิด จะเปิดช่องให้ส่งเส้นทางที่เลี่ยง OA หรือลัดผ่านกำแพงเข้ามา)
/// </summary>
public class MovementReachableEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "movement-reachable-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _heroTokenId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _otherPlayersCharacterId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _otherPlayerUserId;
    private string _otherPlayerToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_otherPlayerUserId, _otherPlayerToken) = await RegisterAsync("other");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Reachable Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignId, _otherPlayerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        // speed 30 ฟุต = 6 ช่อง
        await InsertCharacterAsync(conn, _heroId, "อัลดริค", _playerUserId, speedFeet: 30);
        await InsertCharacterAsync(conn, _otherPlayersCharacterId, "คนอื่น", _otherPlayerUserId, speedFeet: 30);

        var tokens = $$"""
            [{"id":"{{_heroTokenId}}","entityType":"Character","entityId":"{{_heroId}}","x":0,"y":0}]
            """;
        await InsertSceneAsync(conn, _sceneId, _sessionId, tokens);
    }

    /// <summary>ช่องที่ยืนอยู่ต้องอยู่ในผลลัพธ์เสมอที่ราคา 0 — ผู้เล่นต้อง "ปล่อยกลับที่เดิม" ได้
    /// โดยไม่เสียอะไร (ยกเลิกการลาก)</summary>
    [Fact]
    public async Task Origin_is_always_reachable_at_zero_cost()
    {
        var body = await ReachableAsync(_playerToken, _heroId);

        var origin = Assert.Single(body.Squares.Where(s => s.X == 0 && s.Y == 0));
        Assert.Equal(0, origin.CostFeet);
    }

    /// <summary>
    /// speed 30 ฟุต = 6 ช่องทุกทิศ (ทแยงเท่าตรงตาม SRD) → ครอบ 13×13 ช่อง = 169 ช่องรวมจุดเริ่ม
    ///
    /// เลขนี้จงใจตรวจแบบเป๊ะ ๆ เพื่อจับกรณี off-by-one ของขอบเขต — ถ้าขอบเขตกว้าง/แคบไปหนึ่งช่อง
    /// ผู้เล่นจะเห็นพื้นที่ที่ปล่อยแล้วโดนปฏิเสธ หรือปล่อยไม่ได้ทั้งที่กฎอนุญาต
    /// </summary>
    [Fact]
    public async Task Reachable_area_matches_the_characters_speed_exactly()
    {
        var body = await ReachableAsync(_playerToken, _heroId);

        Assert.Equal(30, body.BudgetFeet);
        Assert.Equal(169, body.Squares.Count);
        Assert.Contains(body.Squares, s => s.X == 6 && s.Y == 6 && s.CostFeet == 30);
        Assert.DoesNotContain(body.Squares, s => s.X == 7);
    }

    /// <summary>
    /// ทุกช่องต้องมีเส้นทางติดมาด้วย เริ่มที่จุดยืนปัจจุบันและจบที่ช่องนั้น — **client ไม่ต้องคิด
    /// เส้นทางเอง** แค่ส่งอันที่ได้มากลับไป commit
    /// </summary>
    [Fact]
    public async Task Every_reachable_square_carries_a_server_computed_path()
    {
        var body = await ReachableAsync(_playerToken, _heroId);

        Assert.All(body.Squares, s =>
        {
            Assert.NotEmpty(s.Path);
            Assert.Equal(0, s.Path[0].X);
            Assert.Equal(0, s.Path[0].Y);
            Assert.Equal(s.X, s.Path[^1].X);
            Assert.Equal(s.Y, s.Path[^1].Y);
        });
    }

    /// <summary>
    /// **เทสต์ที่พิสูจน์ว่าฟีเจอร์ลากใช้ได้จริง**: หยิบเส้นทางที่ endpoint นี้คืนมาแล้วส่งเข้า commit
    /// ต้องผ่านและ token ต้องไปอยู่ตรงนั้นจริง
    ///
    /// ถ้าสองฝั่งนี้ไม่ตรงกัน ผู้เล่นจะลากไปวางในพื้นที่ที่ไฮไลต์ว่าไปได้ แล้วโดนปฏิเสธ ซึ่งเป็น
    /// ประสบการณ์ที่แย่ที่สุดของ UI แบบลาก
    /// </summary>
    [Fact]
    public async Task A_path_returned_here_is_accepted_by_commit()
    {
        var body = await ReachableAsync(_playerToken, _heroId);
        var target = body.Squares.Single(s => s.X == 4 && s.Y == 2);

        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/commit",
            new
            {
                CharacterId = _heroId,
                Path = target.Path.Select(p => new { X = (double)p.X, Y = (double)p.Y }).ToArray(),
                Disengage = false,
            });

        response.EnsureSuccessStatusCode();
        Assert.Equal((4, 2), await TokenPositionAsync());
    }

    /// <summary>ดูขอบเขตของตัวละครคนอื่นไม่ได้ — ไม่งั้นจะรู้ว่าคนอื่นเดินไปไหนได้บ้าง</summary>
    [Fact]
    public async Task Reachable_for_another_players_character_is_forbidden()
    {
        var response = await AuthedClient(_playerToken).GetAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/reachable/{_otherPlayersCharacterId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/reachable/{_heroId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<ReachableResponse> ReachableAsync(string token, Guid characterId)
    {
        var response = await AuthedClient(token).GetAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/reachable/{characterId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReachableResponse>(JsonOptions))!;
    }

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
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"reachable-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Reachable {label}",
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

    private static async Task InsertCampaignAsync(
        NpgsqlConnection conn, Guid id, Guid ownerUserId, string name)
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

    private static async Task InsertMembershipAsync(
        NpgsqlConnection conn, Guid campaignId, Guid userId, string role)
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

    private async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, string name, Guid? ownerUserId, int speedFeet)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}'::jsonb, 20, 20, 0, 12, 0, @speedFeet,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("speedFeet", speedFeet);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertSceneAsync(
        NpgsqlConnection conn, Guid id, Guid sessionId, string tokensJson)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@id, @sessionId, 'Reachable Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"55555555-5555-5555-5555-555555555555","x":0,"y":0,"radius":40}]}',
                true)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        cmd.Parameters.AddWithValue("tokens", tokensJson);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaign = new NpgsqlCommand("DELETE FROM campaigns WHERE id = @id", conn))
        {
            deleteCampaign.Parameters.AddWithValue("id", _campaignId);
            await deleteCampaign.ExecuteNonQueryAsync();
        }

        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue(
                "ids", new[] { _dmUserId, _playerUserId, _otherPlayerUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }

        _factory.Dispose();
    }

    private record AuthTokenResponse(string Token, Guid UserId);

    private record PathPointResponse(int X, int Y);

    private record ReachableSquareResponse(int X, int Y, int CostFeet, List<PathPointResponse> Path);

    private record ReachableResponse(Guid CharacterId, int BudgetFeet, List<ReachableSquareResponse> Squares);
}

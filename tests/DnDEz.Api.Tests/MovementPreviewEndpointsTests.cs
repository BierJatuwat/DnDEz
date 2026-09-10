using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 10 — POST /campaigns/{cid}/sessions/{sid}/movement/preview
/// "ถ้าเดินเส้นทางนี้จะเกิดอะไรขึ้นบ้าง" — คิดให้ครบ **ก่อน** ผู้เล่นกด
///
/// spatial-play-design §3.4 เขียนเป็นข้อกำหนดตรง ๆ ว่าระบบต้องบอกก่อนกด:
/// *"ทางนี้ใช้ 25 จาก 30 ฟุต และจะโดนโจมตีสวนตอนออกจากระยะก็อบลิน"* และสรุปว่า
/// **"นี่คือจุดที่ทำให้ OA ไม่ใช่กับดักที่มือใหม่เหยียบโดยไม่รู้ตัว แต่เป็นข้อมูลที่เห็นก่อนตัดสินใจ"**
///
/// endpoint นี้ **ไม่เปลี่ยน state อะไรเลย** — คิดอย่างเดียว ไม่หัก movement ไม่ขยับ token
/// ไม่กิน reaction ของใคร (เรียกซ้ำกี่ครั้งก็ได้ผลเท่าเดิม) การเดินจริงเป็นคนละ endpoint
///
/// ⚠️ **การรั่วของข้อมูลผ่านคำเตือน** — คำเตือนบอกชื่อศัตรู ถ้าเตือนถึงศัตรูที่ผู้เล่นยังมองไม่เห็น
/// ก็เท่ากับบอกว่ามีมันอยู่ (บทเรียนเดียวกับ Part 9 §1.2) · จึงเตือนเฉพาะศัตรูที่อยู่ในพื้นที่ที่เปิด
/// หมอกแล้วเท่านั้น — ศัตรูที่ซุ่มอยู่จะไม่โผล่ในคำเตือน ซึ่งถูกต้องทั้งเรื่องความปลอดภัยและเรื่องเกม
/// (การซุ่มโจมตี *ควร* เซอร์ไพรส์)
/// </summary>
public class MovementPreviewEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "movement-preview-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();

    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _visibleGoblinId = Guid.NewGuid();
    private readonly Guid _hiddenAmbusherId = Guid.NewGuid();
    private readonly Guid _otherPlayersCharacterId = Guid.NewGuid();

    private const string HiddenAmbusherName = "นักซุ่มโจมตีในเงามืด";

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

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Movement Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignId, _otherPlayerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        await InsertCharacterAsync(conn, _heroId, "อัลดริค", _playerUserId, controlledByDm: false, speedFeet: 30);
        await InsertCharacterAsync(conn, _visibleGoblinId, "ก็อบลินยาม", null, controlledByDm: true, speedFeet: 30);
        await InsertCharacterAsync(conn, _hiddenAmbusherId, HiddenAmbusherName, null, controlledByDm: true, speedFeet: 30);
        await InsertCharacterAsync(
            conn, _otherPlayersCharacterId, "คนอื่น", _otherPlayerUserId, controlledByDm: false, speedFeet: 30);

        // หมอกเปิดวงเดียวรอบจุดเริ่ม — ก็อบลินยามอยู่ในวง, นักซุ่มโจมตีที่ (2,1) อยู่นอกวง
        var tokens = $$"""
            [
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_heroId}}","x":0,"y":0},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_visibleGoblinId}}","x":1,"y":1},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_hiddenAmbusherId}}","x":40,"y":40}
            ]
            """;
        await InsertSceneAsync(conn, _sceneId, _sessionId, tokens);
    }

    // ── DoD ข้อ (ข) ครึ่งหลัง: ต้องเห็นคำเตือน "ก่อนกด" ──────────────────────

    /// <summary>
    /// **หัวใจของ §3.4** — เดินออกจากระยะเอื้อมของก็อบลิน แล้วต้องได้คำเตือนกลับมาพร้อมชื่อศัตรู
    /// ตั้งแต่ตอน preview คือ *ก่อน* จะเกิดอะไรขึ้นจริง
    ///
    /// ฮีโร่ (0,0) · ก็อบลิน (1,1) เอื้อม 5 ฟุต · เดิน (0,0)→(1,0)→(2,0)→(3,0)
    /// สามช่องแรกยังห่างก็อบลิน 1 ช่อง พอถึง (3,0) ห่าง 2 ช่อง = หลุดระยะ → โดนสวน
    /// </summary>
    [Fact]
    public async Task Walking_out_of_reach_warns_before_committing()
    {
        var body = await PreviewAsync(_playerToken, _heroId, [(0, 0), (1, 0), (2, 0), (3, 0)]);

        var warning = Assert.Single(body.Warnings);
        Assert.Equal(_visibleGoblinId, warning.ThreatId);
        Assert.Equal("ก็อบลินยาม", warning.ThreatName);
        // การสวนเกิดตอนอยู่ช่องสุดท้ายที่ยังอยู่ในระยะ (2,0) ไม่ใช่ช่องปลายทาง
        Assert.Equal(2, warning.AttackFromX);
        Assert.Equal(0, warning.AttackFromY);
        // ข้อความต้องอ่านรู้เรื่องเองโดยไม่ต้องรู้จักคำว่า "opportunity attack"
        Assert.False(string.IsNullOrWhiteSpace(warning.Message));
        Assert.Contains("ก็อบลินยาม", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>เดินอยู่ในระยะตลอด ไม่หลุดออกไป = ไม่มีคำเตือน (ไม่ใช่เตือนไว้ก่อนทุกครั้ง
    /// จนผู้เล่นเลิกอ่าน)</summary>
    [Fact]
    public async Task Staying_within_reach_produces_no_warning()
    {
        var body = await PreviewAsync(_playerToken, _heroId, [(0, 0), (1, 0), (2, 1)]);

        Assert.Empty(body.Warnings);
    }

    /// <summary>เลือก Disengage = ไม่โดนสวน ผู้เล่นจึงเปรียบเทียบสองทางเลือกได้ก่อนตัดสินใจ
    /// (ตรงกับ §3.4 ที่ว่าต้องเห็นความต่างระหว่าง "ทางที่ถูกที่สุด" กับ "ทางที่ปลอดภัยที่สุด")</summary>
    [Fact]
    public async Task Disengaging_removes_the_warning()
    {
        var body = await PreviewAsync(_playerToken, _heroId, [(0, 0), (1, 0), (2, 0), (3, 0)], disengage: true);

        Assert.Empty(body.Warnings);
    }

    /// <summary>
    /// **เทสต์กันข้อมูลรั่วผ่านคำเตือน** — ศัตรูที่ยังอยู่นอกพื้นที่เปิดหมอกต้องไม่โผล่ในคำเตือนเลย
    /// ตรวจที่ raw JSON เพราะช่องรั่วที่อันตรายที่สุดคือช่องที่ไม่ได้นึกถึงตอนเขียน assertion
    /// </summary>
    [Fact]
    public async Task Preview_never_mentions_an_enemy_hidden_by_fog()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(PreviewUrl(), new
        {
            CharacterId = _heroId,
            Path = new[] { new { X = 0.0, Y = 0.0 }, new { X = 1.0, Y = 0.0 } },
        });
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(_hiddenAmbusherId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HiddenAmbusherName, raw, StringComparison.Ordinal);
    }

    // ── ต้นทุนการเดิน (ต่อยอด geometry ของ Part 8) ───────────────────────────

    /// <summary>ราคาเดินคิดจาก <c>MovementCostResolver</c> ของ Part 8 — 3 ก้าวบนพื้นโล่ง = 15 ฟุต
    /// และเทียบกับ speed จริงของตัวละคร (30) ว่ายังจ่ายไหว</summary>
    [Fact]
    public async Task Preview_reports_movement_cost_and_affordability()
    {
        var body = await PreviewAsync(_playerToken, _heroId, [(0, 0), (1, 0), (2, 0), (3, 0)]);

        Assert.Equal(15, body.CostFeet);
        Assert.True(body.IsAffordable);
    }

    /// <summary>เส้นทางยาวเกิน speed = บอกว่าจ่ายไม่ไหว **แต่ยังบอกราคาจริง** ให้ผู้เล่นรู้ว่าขาดเท่าไร
    /// ไม่ใช่ปฏิเสธเฉย ๆ</summary>
    [Fact]
    public async Task Path_longer_than_speed_is_reported_as_unaffordable_with_the_real_cost()
    {
        var path = Enumerable.Range(0, 9).Select(i => (i, 0)).ToArray();

        var body = await PreviewAsync(_playerToken, _heroId, path);

        Assert.Equal(40, body.CostFeet);
        Assert.False(body.IsAffordable);
    }

    /// <summary>ก้าวที่ไม่ติดกันคือ input ผิดสัญญา → 400 ไม่ใช่ 500 (Part 8 โยน ArgumentException
    /// endpoint ต้องแปลงเป็นคำตอบที่ถูกต้อง)</summary>
    [Fact]
    public async Task Non_contiguous_path_returns_400()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(PreviewUrl(), new
        {
            CharacterId = _heroId,
            Path = new[] { new { X = 0.0, Y = 0.0 }, new { X = 9.0, Y = 9.0 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── สิทธิ์ ────────────────────────────────────────────────────────────────

    /// <summary>ดู preview ของตัวละครคนอื่นไม่ได้ — ไม่งั้นจะกลายเป็นช่องส่องว่าคนอื่นมองเห็นอะไร</summary>
    [Fact]
    public async Task Previewing_another_players_character_gets_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(PreviewUrl(), new
        {
            CharacterId = _otherPlayersCharacterId,
            Path = new[] { new { X = 0.0, Y = 0.0 }, new { X = 1.0, Y = 0.0 } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>DM คุม NPC ของตัวเองได้ — NPC ก็ต้องรู้ว่าเดินแล้วจะโดนสวนเหมือนกัน</summary>
    [Fact]
    public async Task Dm_can_preview_movement_for_an_npc_they_control()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(PreviewUrl(), new
        {
            CharacterId = _visibleGoblinId,
            Path = new[] { new { X = 1.0, Y = 1.0 }, new { X = 2.0, Y = 1.0 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string PreviewUrl() => $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/preview";

    private async Task<PreviewResponse> PreviewAsync(
        string token, Guid characterId, (int X, int Y)[] path, bool disengage = false)
    {
        var response = await AuthedClient(token).PostAsJsonAsync(PreviewUrl(), new
        {
            CharacterId = characterId,
            Path = path.Select(p => new { X = (double)p.X, Y = (double)p.Y }).ToArray(),
            Disengage = disengage,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PreviewResponse>(JsonOptions))!;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"movement-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Movement {label}",
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
        NpgsqlConnection conn, Guid id, string name, Guid? ownerUserId, bool controlledByDm, int speedFeet)
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
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @controlledByDm, false,
                 false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("controlledByDm", controlledByDm);
        cmd.Parameters.AddWithValue("speedFeet", speedFeet);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertSceneAsync(
        NpgsqlConnection conn, Guid id, Guid sessionId, string tokensJson)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@id, @sessionId, 'Movement Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"33333333-3333-3333-3333-333333333333","x":0,"y":0,"radius":12}]}',
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
            deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId, _otherPlayerUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }

        _factory.Dispose();
    }

    // ── response contracts (นิยามในเทสต์เอง ไม่พึ่ง internal type ของ Api) ──

    private record AuthTokenResponse(string Token, Guid UserId);

    private record MovementWarningResponse(
        Guid ThreatId,
        string ThreatName,
        int LeavesAtStepIndex,
        int AttackFromX,
        int AttackFromY,
        string Message);

    private record PreviewResponse(
        Guid CharacterId,
        int CostFeet,
        bool IsAffordable,
        bool RequiresLandingCheck,
        List<MovementWarningResponse> Warnings);
}

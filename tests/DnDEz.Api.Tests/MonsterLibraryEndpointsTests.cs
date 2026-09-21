using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// คลังมอนสเตอร์ของแคมเปญ — สองชิ้นที่ทำให้ "เลือกมอนสเตอร์จาก SRD แล้วเอาลงแผนที่" ครบวงจร:
///
/// 1. `GET /campaigns/{cid}/monsters` [DM เท่านั้น] — รายชื่อแม่แบบในแคมเปญ (ก่อนหน้านี้ไม่มีเส้นทางเลย
///    จอ DM จึงเลือกมอนสเตอร์มาวางไม่ได้ ทั้งที่ endpoint วาง token รับ Monster มาตั้งแต่ Sprint G Part 5)
///    403 = สมาชิกที่ไม่ใช่ DM · 404 = คนนอกแคมเปญ · 401 = ไม่ล็อกอิน
///
/// 2. `TokenResponse.Name` ใน `GET .../scene` — ชื่อของ token ที่ **ผ่านด่านหมอกมาแล้ว** ทั้ง Character
///    และ Monster · เดิมหน้าจอหาชื่อจากลิสต์ตัวละครเอง ซึ่งไม่มีมอนสเตอร์อยู่ในนั้น จึงขึ้นว่า "Monster"
///
/// ⚠️ เทสต์ adversarial ที่ล็อกข้อ 2: ชื่อมอนสเตอร์ที่ยังอยู่ในหมอก **ห้ามปรากฏใน raw JSON ของผู้เล่น**
/// (รูปแบบเดียวกับ `SceneFogEndpointsTests.Player_response_raw_json_contains_no_trace_of_hidden_token`)
///
/// ห้ามแก้เทสต์เพื่อให้ผ่าน
/// </summary>
public class MonsterLibraryEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "monster-library-secret-1";

    // ชื่อที่ไม่ซ้ำกับอะไรในระบบ — ใช้ค้นใน raw JSON ได้โดยไม่ชนคำอื่นโดยบังเอิญ
    private const string VisibleMonsterName = "ก็อบลินยามประตู";
    private const string HiddenMonsterName = "อสูรซุ่มในหมอก";
    private const string NpcName = "พ่อค้าเร่";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _npcId = Guid.NewGuid();
    private readonly Guid _visibleMonsterId = Guid.NewGuid();
    private readonly Guid _hiddenMonsterId = Guid.NewGuid();
    private readonly Guid _otherCampaignMonsterId = Guid.NewGuid();

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

        await ExecAsync(conn,
            """
            INSERT INTO campaigns (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@c, @dm, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now()),
                   (@other, @outsider, @otherName, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now());
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (gen_random_uuid(), @c, @dm, 'Dm', now()),
                   (gen_random_uuid(), @c, @player, 'Player', now()),
                   (gen_random_uuid(), @other, @outsider, 'Dm', now());
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@s, @c, 'Online', 'Active', NULL, now(), NULL);
            """,
            ("c", _campaignId), ("dm", _dmUserId), ("player", _playerUserId), ("s", _sessionId),
            ("other", _otherCampaignId), ("outsider", _outsiderUserId),
            ("name", $"Monster Library {_suffix}"), ("otherName", $"Other {_suffix}"));

        await InsertMonsterAsync(conn, _visibleMonsterId, _campaignId, VisibleMonsterName,
            ac: 15, hp: 10, speed: 30, reach: 5, srdKey: "goblin-warrior", reviewStatus: "Approved");
        await InsertMonsterAsync(conn, _hiddenMonsterId, _campaignId, HiddenMonsterName,
            ac: 17, hp: 45, speed: 40, reach: 10, srdKey: null, reviewStatus: "Pending");
        await InsertMonsterAsync(conn, _otherCampaignMonsterId, _otherCampaignId, "ของแคมเปญอื่น",
            ac: 11, hp: 5, speed: 30, reach: 5, srdKey: null, reviewStatus: "Approved");

        await InsertCharacterAsync(conn, _heroId, "ฮีโร่", _playerUserId, controlledByDm: false);
        await InsertCharacterAsync(conn, _npcId, NpcName, ownerUserId: null, controlledByDm: true);

        // หมอกเปิดวงเดียวรอบ (0,0) รัศมี 10 — (2,2) อยู่ใน · (99,99) อยู่นอก
        var tokens = $$"""
            [
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_heroId}}","x":1,"y":1},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_npcId}}","x":3,"y":3},
                {"id":"{{Guid.NewGuid()}}","entityType":"Monster","entityId":"{{_visibleMonsterId}}","x":2,"y":2},
                {"id":"{{Guid.NewGuid()}}","entityType":"Monster","entityId":"{{_hiddenMonsterId}}","x":99,"y":99}
            ]
            """;
        await ExecAsync(conn,
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@sc, @s, 'Monster Library Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"77777777-7777-7777-7777-777777777777","x":0,"y":0,"radius":10}]}', true)
            """,
            ("sc", _sceneId), ("s", _sessionId), ("tokens", tokens));
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)",
            ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId }));
        await _factory.DisposeAsync();
    }

    // ═══ GET /campaigns/{cid}/monsters ═══════════════════════════════════════

    [Fact]
    public async Task Dm_sees_every_monster_of_the_campaign_sorted_by_name()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/monsters");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var monsters = await response.Content.ReadFromJsonAsync<List<MonsterItem>>(JsonOptions);

        Assert.Equal(2, monsters!.Count);
        Assert.Equal(new[] { HiddenMonsterName, VisibleMonsterName }.OrderBy(n => n, StringComparer.Ordinal),
            monsters.Select(m => m.Name));

        var goblin = monsters.Single(m => m.Id == _visibleMonsterId);
        Assert.Equal((15, 10, 30, 5, "goblin-warrior", "Approved"),
            (goblin.ArmorClass, goblin.HitPoints, goblin.SpeedFeet, goblin.ReachFeet, goblin.SrdKey, goblin.ReviewStatus));
    }

    /// <summary>
    /// เหมือน scene-prep: จอนี้เป็นจอของ DM เอง ถ้ากรองเฉพาะ Approved ออกไป ของที่รอรีวิวจะหายจากสายตา
    /// จนรีวิวไม่ได้ · srdKey เป็น null สำหรับแม่แบบที่ไม่ได้มาจากคลัง SRD
    /// </summary>
    [Fact]
    public async Task Monsters_pending_review_are_listed_too_and_srd_key_may_be_null()
    {
        var monsters = await GetMonstersAsync(_dmToken, _campaignId);
        var pending = monsters.Single(m => m.Id == _hiddenMonsterId);
        Assert.Equal("Pending", pending.ReviewStatus);
        Assert.Null(pending.SrdKey);
    }

    [Fact]
    public async Task Monsters_of_another_campaign_never_leak_into_this_list()
    {
        var monsters = await GetMonstersAsync(_dmToken, _campaignId);
        Assert.DoesNotContain(monsters, m => m.Id == _otherCampaignMonsterId);
    }

    [Fact]
    public async Task A_player_cannot_read_the_monster_list()
    {
        // AC/HP ของศัตรูคือสิ่งที่ผู้เล่นต้องค้นหาเอาในเกม — เหตุผลเดียวกับ scene-prep ที่เป็น DM-only
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/monsters");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_outsider_gets_404_not_403()
    {
        // 404 ไม่ใช่ 403 — คนนอกไม่ควรรู้ด้วยซ้ำว่าแคมเปญนี้มีอยู่จริง (แบบแผนเดิมทั้งระบบ)
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/monsters");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reading_the_monster_list_requires_login()
    {
        var response = await _factory.CreateClient().GetAsync($"/campaigns/{_campaignId}/monsters");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═══ PATCH /campaigns/{cid}/monsters/{id} — DM ปรับค่าพลังก่อนเอาลงแผนที่ ═══

    [Fact]
    public async Task Dm_can_adjust_every_stat_the_engine_uses()
    {
        var response = await PatchMonsterAsync(_dmToken, _visibleMonsterId, new
        {
            name = "ก็อบลินยามประตู (ตัวหัวหน้า)",
            armorClass = 17,
            hitPoints = 25,
            speedFeet = 40,
            reachFeet = 10,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MonsterItem>(JsonOptions);
        Assert.Equal(("ก็อบลินยามประตู (ตัวหัวหน้า)", 17, 25, 40, 10),
            (body!.Name, body.ArmorClass, body.HitPoints, body.SpeedFeet, body.ReachFeet));

        // ต้องลงฐานจริง ไม่ใช่แค่ response สวย
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT name, armor_class, hit_points, speed_feet, reach_feet FROM monster_stat_blocks WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _visibleMonsterId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(("ก็อบลินยามประตู (ตัวหัวหน้า)", 17, 25, 40, 10),
            (r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
    }

    [Fact]
    public async Task Fields_that_are_not_sent_keep_their_value()
    {
        // PATCH ไม่ใช่ PUT — ส่งมาแค่ HP ต้องไม่ล้างค่าอื่นเป็น 0/null
        var response = await PatchMonsterAsync(_dmToken, _visibleMonsterId, new { hitPoints = 33 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MonsterItem>(JsonOptions);
        Assert.Equal(33, body!.HitPoints);
        Assert.Equal((VisibleMonsterName, 15, 30, 5), (body.Name, body.ArmorClass, body.SpeedFeet, body.ReachFeet));
        Assert.Equal("goblin-warrior", body.SrdKey);
    }

    [Fact]
    public async Task Adjusted_reach_is_what_opportunity_attacks_will_use()
    {
        // reach คือค่าที่ Sprint H Part 7 ใช้ตัดสิน OA จริง — แก้แล้วต้องมีผลกับกลไก ไม่ใช่แค่ตัวเลขโชว์
        await PatchMonsterAsync(_dmToken, _visibleMonsterId, new { reachFeet = 15 });
        var monsters = await GetMonstersAsync(_dmToken, _campaignId);
        Assert.Equal(15, monsters.Single(m => m.Id == _visibleMonsterId).ReachFeet);
    }

    [Theory]
    // AC/HP ต่ำกว่า 1 ไม่มีความหมายในกฎ · reach ต่ำกว่า 5 ขัด SRD "Reach" ตรง ๆ · ค่าติดลบคือพิมพ์พลาด
    [InlineData("{\"armorClass\":0}", "invalid_armor_class")]
    [InlineData("{\"armorClass\":-5}", "invalid_armor_class")]
    [InlineData("{\"armorClass\":100}", "invalid_armor_class")]
    [InlineData("{\"hitPoints\":0}", "invalid_hit_points")]
    [InlineData("{\"hitPoints\":-1}", "invalid_hit_points")]
    [InlineData("{\"speedFeet\":-1}", "invalid_speed")]
    [InlineData("{\"reachFeet\":0}", "invalid_reach")]
    [InlineData("{\"reachFeet\":4}", "invalid_reach")]
    [InlineData("{\"name\":\"\"}", "invalid_name")]
    [InlineData("{\"name\":\"   \"}", "invalid_name")]
    public async Task Invalid_values_are_400_with_a_reason_and_change_nothing(string json, string reason)
    {
        var response = await AuthedClient(_dmToken).PatchAsync(
            $"/campaigns/{_campaignId}/monsters/{_visibleMonsterId}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions);
        Assert.Equal(reason, body!.Reason);

        // 400 แล้วต้องไม่มีอะไรเปลี่ยน — ไม่ใช่เขียนไปครึ่งหนึ่งแล้วค่อยปฏิเสธ
        var monsters = await GetMonstersAsync(_dmToken, _campaignId);
        var unchanged = monsters.Single(m => m.Id == _visibleMonsterId);
        Assert.Equal((VisibleMonsterName, 15, 10, 30, 5),
            (unchanged.Name, unchanged.ArmorClass, unchanged.HitPoints, unchanged.SpeedFeet, unchanged.ReachFeet));
    }

    [Fact]
    public async Task Speed_zero_is_allowed_because_some_creatures_cannot_move()
    {
        var response = await PatchMonsterAsync(_dmToken, _visibleMonsterId, new { speedFeet = 0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MonsterItem>(JsonOptions);
        Assert.Equal(0, body!.SpeedFeet);
    }

    [Fact]
    public async Task Name_is_trimmed_before_saving()
    {
        var response = await PatchMonsterAsync(_dmToken, _visibleMonsterId, new { name = "  ผีเฝ้าสมบัติ  " });
        var body = await response.Content.ReadFromJsonAsync<MonsterItem>(JsonOptions);
        Assert.Equal("ผีเฝ้าสมบัติ", body!.Name);
    }

    [Fact]
    public async Task A_player_cannot_adjust_monster_stats()
    {
        var response = await PatchMonsterAsync(_playerToken, _visibleMonsterId, new { hitPoints = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_dm_of_another_campaign_cannot_adjust_this_campaigns_monster()
    {
        var response = await PatchMonsterAsync(_outsiderToken, _visibleMonsterId, new { hitPoints = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Adjusting_a_monster_of_another_campaign_by_id_is_404_not_a_silent_edit()
    {
        // id มีจริงแต่เป็นของแคมเปญอื่น — ต้องไม่ถูกแก้ผ่าน route ของแคมเปญนี้
        var response = await PatchMonsterAsync(_dmToken, _otherCampaignMonsterId, new { hitPoints = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT hit_points FROM monster_stat_blocks WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _otherCampaignMonsterId);
        Assert.Equal(5, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Adjusting_requires_login()
    {
        var response = await _factory.CreateClient().PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_visibleMonsterId}", new { hitPoints = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_monster_id_is_404()
    {
        var response = await PatchMonsterAsync(_dmToken, Guid.NewGuid(), new { hitPoints = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// ชั้นสุดท้ายที่ไม่พึ่ง validate ของ endpoint เลย — ถ้ามีใครเพิ่มเส้นทางเขียนใหม่แล้วลืม validate
    /// ฐานต้องยังปฏิเสธเอง (CHECK `ck_monster_stat_block_numbers`)
    /// </summary>
    [Theory]
    [InlineData("armor_class = 0")]
    [InlineData("hit_points = 0")]
    [InlineData("speed_feet = -1")]
    [InlineData("reach_feet = 4")]
    public async Task The_database_itself_rejects_impossible_numbers(string assignment)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"UPDATE monster_stat_blocks SET {assignment} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _visibleMonsterId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    // ═══ ชื่อของ token ใน GET .../scene ══════════════════════════════════════

    [Fact]
    public async Task Dm_sees_names_for_both_character_and_monster_tokens()
    {
        var scene = await GetSceneAsync(_dmToken);
        Assert.Equal(VisibleMonsterName, scene.Tokens.Single(t => t.EntityId == _visibleMonsterId).Name);
        Assert.Equal(HiddenMonsterName, scene.Tokens.Single(t => t.EntityId == _hiddenMonsterId).Name);
        Assert.Equal("ฮีโร่", scene.Tokens.Single(t => t.EntityId == _heroId).Name);
    }

    [Fact]
    public async Task Player_sees_the_name_of_a_monster_that_is_out_of_the_fog()
    {
        // ชื่อของสิ่งที่มองเห็นได้ไม่ใช่ความลับ — affordance ส่งชื่อเป้าให้ผู้เล่นอยู่แล้วตั้งแต่ Sprint H Part 7
        var scene = await GetSceneAsync(_playerToken);
        Assert.Equal(VisibleMonsterName, scene.Tokens.Single(t => t.EntityId == _visibleMonsterId).Name);
    }

    [Fact]
    public async Task Player_still_sees_the_name_of_an_npc_whose_stats_are_hidden()
    {
        // ชื่อกับ stat แยกกัน: NPC ที่ปิด stat ไว้ยังต้องมีชื่อให้เรียก (ไม่งั้นบนแผนที่เป็นวงกลมไร้ชื่อ)
        var scene = await GetSceneAsync(_playerToken);
        var npc = scene.Tokens.Single(t => t.EntityId == _npcId);
        Assert.Equal(NpcName, npc.Name);
        Assert.Null(npc.CurrentHp);
    }

    [Fact]
    public async Task Player_response_raw_json_contains_no_trace_of_a_monster_hidden_by_fog()
    {
        var response = await AuthedClient(_playerToken).GetAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        var raw = await response.Content.ReadAsStringAsync();

        // adversarial — ตรวจ raw string ไม่ใช่ deserialize แล้วเช็คฟิลด์ (กัน serializer เผลอ include
        // อะไรที่โมเดล C# ไม่ได้ตั้งใจ) · ทั้ง id · ชื่อ · และตัวเลข stat ที่มีแต่ตัวนี้เท่านั้นที่มี
        Assert.DoesNotContain(_hiddenMonsterId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HiddenMonsterName, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"99\"", raw, StringComparison.Ordinal);
        Assert.Contains(VisibleMonsterName, raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// ฉากที่ DM เพิ่งสร้าง/เพิ่งจัดกริด ใช้ตัวประกอบ response **ตัวเดียวกับ** GET — ก่อนหน้านี้มี builder
    /// สองตัวแยกกัน ซึ่งแปลว่าฟิลด์ใหม่ทุกฟิลด์ต้องไปเติมสองที่และจะเงียบถ้าลืมที่ใดที่หนึ่ง (§2.4)
    /// </summary>
    [Fact]
    public async Task Setting_the_grid_returns_tokens_with_names_too()
    {
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{_sceneId}/grid",
            new { PixelsPerSquare = 64, OffsetX = 0, OffsetY = 0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);
        Assert.Equal(VisibleMonsterName, scene!.Tokens.Single(t => t.EntityId == _visibleMonsterId).Name);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private record MonsterItem(
        Guid Id, string Name, int ArmorClass, int HitPoints, int SpeedFeet, int ReachFeet,
        string? SrdKey, string ReviewStatus);

    private record TokenResponse(
        Guid Id, string EntityType, Guid EntityId, double X, double Y, int? CurrentHp, int? MaxHp, string? Name);

    private record SceneResponse(Guid Id, List<TokenResponse> Tokens);

    private record AuthResponse(string Token, Guid UserId);

    private record ErrorBody(string Reason);

    private async Task<HttpResponseMessage> PatchMonsterAsync(string token, Guid monsterId, object body) =>
        await AuthedClient(token).PatchAsJsonAsync($"/campaigns/{_campaignId}/monsters/{monsterId}", body);

    private async Task<List<MonsterItem>> GetMonstersAsync(string token, Guid campaignId)
    {
        var response = await AuthedClient(token).GetAsync($"/campaigns/{campaignId}/monsters");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<MonsterItem>>(JsonOptions))!;
    }

    private async Task<SceneResponse> GetSceneAsync(string token)
    {
        var response = await AuthedClient(token).GetAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions))!;
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"monster-library-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"MonsterLibrary {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private async Task InsertMonsterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name,
        int ac, int hp, int speed, int reach, string? srdKey, string reviewStatus) =>
        await ExecAsync(conn,
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status,
                 speed_feet, reach_feet, srd_key)
            VALUES (@id, @c, @name, @ac, @hp, '[]'::jsonb, '[]'::jsonb, @status, @speed, @reach, @srdKey)
            """,
            ("id", id), ("c", campaignId), ("name", name), ("ac", ac), ("hp", hp),
            ("status", reviewStatus), ("speed", speed), ("reach", reach),
            ("srdKey", (object?)srdKey ?? DBNull.Value));

    private async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, string name, Guid? ownerUserId, bool controlledByDm) =>
        await ExecAsync(conn,
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at, strength, dexterity, constitution, intelligence, wisdom, charisma)
            VALUES (@id, @c, @owner, @name, '{}'::jsonb, 20, 20, 0, 12, 0, 30,
                    ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @dm, false, false, false, now(),
                    15, 13, 14, 12, 10, 8)
            """,
            ("id", id), ("c", _campaignId), ("owner", (object?)ownerUserId ?? DBNull.Value),
            ("name", name), ("dm", controlledByDm));

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}

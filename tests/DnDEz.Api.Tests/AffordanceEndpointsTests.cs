using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 9 — GET /campaigns/{cid}/sessions/{sid}/affordances/{characterId}
/// "ตาฉันแล้ว ทำอะไรได้บ้าง" คำนวณที่ server (spatial-play-design §1.2)
///
/// **เหตุผลที่ endpoint นี้ต้องมีเทสต์ security เป็นตัวหลัก ไม่ใช่ของแถม** — §1.2 เขียนไว้ว่า
/// *"รายการ 'สิ่งที่ทำได้' เป็นช่องรั่วของข้อมูลในตัวมันเอง ถ้าพาเนลบอกว่า 'โจมตีก็อบลินได้'
/// ผู้เล่นก็รู้แล้วว่ามีก็อบลินอยู่"* — ของที่รั่วได้คราวนี้คือ **"มีอยู่ไหม"** ซึ่งแรงกว่า
/// **"เลขเท่าไร"** ที่ Sprint F งานที่ 7 เคยพลาด
///
/// ฉากทดสอบ: หมอกเปิดวงเดียวที่ (0,0) รัศมี 10
///   • Hero (ตัวละครผู้เล่น) ที่ (1,1) — ถือดาบยาว (ประชิด 5 ฟุต) + ธนูสั้น (80/320)
///   • ก็อบลินที่เห็นได้ ที่ (2,2) — อยู่ในวงหมอก ห่าง 1 ช่อง = 5 ฟุต (ประชิด)
///   • **ก็อบลินที่ซ่อนอยู่ ที่ (99,99)** — นอกวงหมอก ผู้เล่นยังไม่รู้ว่ามีตัวตน
///
/// พิกัด token นับเป็น "ช่อง" ระยะเป็นฟุตคิดแบบ Chebyshev × 5 ตาม SRD 5.2
/// (*"count squares from a square adjacent to one of them"*) — เลนส์เดียวกับ Part 8
/// </summary>
public class AffordanceEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "affordance-endpoints-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();

    private readonly Guid _heroCharacterId = Guid.NewGuid();
    private readonly Guid _visibleGoblinCharacterId = Guid.NewGuid();
    private readonly Guid _hiddenGoblinCharacterId = Guid.NewGuid();
    private readonly Guid _otherPlayersCharacterId = Guid.NewGuid();

    private const string HiddenGoblinName = "ก็อบลินซุ่มโจมตี";

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _otherPlayerUserId;
    private string _otherPlayerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    private Guid _longswordCatalogId;
    private Guid _shortbowCatalogId;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_otherPlayerUserId, _otherPlayerToken) = await RegisterAsync("other-player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // id ของ catalog ถูก gen_random_uuid() ตอน migrate จึงต่างกันทุกเครื่อง — ต้องหาจาก key
        // ไม่ใช่ hardcode (Part 1 seed ไว้ด้วย key คงที่ 'longsword'/'shortbow')
        _longswordCatalogId = await LookupCatalogIdAsync(conn, "longsword");
        _shortbowCatalogId = await LookupCatalogIdAsync(conn, "shortbow");

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Affordance Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignId, _otherPlayerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        var heroSheet = $$"""
            {"actionCatalogRefs":["{{_longswordCatalogId}}","{{_shortbowCatalogId}}"]}
            """;

        await InsertCharacterAsync(conn, _heroCharacterId, "Hero", _playerUserId, controlledByDm: false, heroSheet);
        await InsertCharacterAsync(conn, _visibleGoblinCharacterId, "ก็อบลินยาม", null, controlledByDm: true, "{}");
        await InsertCharacterAsync(conn, _hiddenGoblinCharacterId, HiddenGoblinName, null, controlledByDm: true, "{}");
        await InsertCharacterAsync(
            conn, _otherPlayersCharacterId, "Someone Else", _otherPlayerUserId, controlledByDm: false, heroSheet);

        var tokens = $$"""
            [
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_heroCharacterId}}","x":1,"y":1},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_visibleGoblinCharacterId}}","x":2,"y":2},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_hiddenGoblinCharacterId}}","x":99,"y":99}
            ]
            """;
        await InsertSceneAsync(conn, _sceneId, _sessionId, tokens);
    }

    // ── ความปลอดภัย: รายการตัวเลือกต้องไม่รั่วว่ามีอะไรอยู่ในหมอก ──────────────

    /// <summary>
    /// **เทสต์ adversarial ที่ dispatch สั่งให้มีโดยเฉพาะ** — ยิง endpoint ตรง ๆ ด้วย token ของผู้เล่น
    /// แล้วตรวจ **raw JSON** ว่าไม่มีร่องรอยของศัตรูที่ยังอยู่ในหมอกเลยแม้แต่ตัวอักษรเดียว
    ///
    /// ตรวจที่ระดับข้อความดิบ ไม่ใช่ deserialize แล้วดูเฉพาะ field ที่นึกออก — เพราะช่องรั่วที่อันตราย
    /// ที่สุดคือช่องที่เราไม่ได้นึกถึงตอนเขียน assertion (เช่นชื่อโผล่ในข้อความป้ายเหตุผล หรือ id
    /// ติดมากับ field ที่เพิ่งเพิ่มทีหลัง)
    /// </summary>
    [Fact]
    public async Task Player_response_raw_json_contains_no_trace_of_enemies_hidden_by_fog()
    {
        var response = await AuthedClient(_playerToken).GetAsync(AffordanceUrl(_heroCharacterId));
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(_hiddenGoblinCharacterId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HiddenGoblinName, raw, StringComparison.Ordinal);
    }

    /// <summary>DM เห็นทุกอย่างอยู่แล้ว — ยืนยันว่าศัตรูตัวที่ผู้เล่นมองไม่เห็น **มีอยู่จริง**
    /// ในฉาก ไม่ใช่เทสต์ข้างบนผ่านเพราะฉากว่างเปล่า (เทสต์ที่ผ่านด้วยเหตุผลผิดอันตรายกว่าไม่มีเทสต์)
    ///
    /// ก็อบลินที่ซ่อนอยู่ที่ (99,99) ห่าง Hero 98 ช่อง = 490 ฟุต ซึ่งเกินระยะไกลสุดของธนูสั้น (320)
    /// จึงไม่โผล่เป็นตัวเลือกแม้แต่ของ DM — ยืนยันการมีอยู่ผ่าน endpoint ฉากแทน</summary>
    [Fact]
    public async Task Hidden_enemy_really_exists_in_the_scene_for_the_dm()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains(_hiddenGoblinCharacterId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── โมเดล 3 ระดับผ่าน HTTP จริง ──────────────────────────────────────────

    /// <summary>ก็อบลินยามอยู่ห่าง 1 ช่อง (5 ฟุต) → ดาบยาวใช้ได้ปกติ ไม่มีป้ายเตือน (ระดับ 3)</summary>
    [Fact]
    public async Task Player_gets_melee_option_against_the_adjacent_visible_enemy()
    {
        var body = await GetAffordancesAsync(_playerToken, _heroCharacterId);

        var melee = Assert.Single(body.Options.Where(
            o => o.OptionId == _longswordCatalogId && o.TargetId == _visibleGoblinCharacterId));
        Assert.Equal("None", melee.AdvantageMode);
        Assert.Empty(melee.Notes);
    }

    /// <summary>ยิงธนูตอนมีศัตรูประชิด → อยู่ในรายการ (ระดับ 2) พร้อมป้ายเหตุผลภาษาคน
    /// ไม่ใช่หายไปเฉย ๆ และไม่ใช่ให้กดแล้วค่อยรู้ทีหลังว่าเสียเปรียบ</summary>
    [Fact]
    public async Task Player_gets_ranged_option_flagged_with_a_human_readable_reason()
    {
        var body = await GetAffordancesAsync(_playerToken, _heroCharacterId);

        var ranged = Assert.Single(body.Options.Where(
            o => o.OptionId == _shortbowCatalogId && o.TargetId == _visibleGoblinCharacterId));
        Assert.Equal("Disadvantage", ranged.AdvantageMode);
        var note = Assert.Single(ranged.Notes);
        Assert.False(string.IsNullOrWhiteSpace(note.Message));
        // ข้อความต้องเป็นภาษาไทยที่ผู้เล่นอ่านเข้าใจ ไม่ใช่รหัสดิบส่งไปให้ client แปลเอง
        Assert.NotEqual(note.Code, note.Message);
    }

    /// <summary>ตัวเลือกทุกอันต้องเล็งเป้าที่ผู้เล่นมองเห็นเท่านั้น — ไล่ตรวจทุกแถวไม่ใช่แค่แถวที่
    /// นึกออก กันกรณีมีตัวเลือกแปลกปลอมโผล่มาทีหลัง</summary>
    [Fact]
    public async Task Every_option_returned_to_the_player_targets_only_visible_entities()
    {
        var body = await GetAffordancesAsync(_playerToken, _heroCharacterId);

        Assert.NotEmpty(body.Options);
        Assert.All(body.Options, o =>
            Assert.True(o.TargetId is null || o.TargetId == _visibleGoblinCharacterId,
                $"ตัวเลือก {o.OptionName} เล็งเป้า {o.TargetId} ซึ่งผู้เล่นไม่ควรมองเห็น"));
    }

    /// <summary>
    /// <c>sheet</c> เป็น jsonb อิสระที่ไม่มี constraint บังคับรูปร่าง — ถ้าวันหนึ่งมีข้อมูลที่ root
    /// ไม่ใช่ object (pipeline นำเข้า, endpoint ในอนาคต, แก้มือใน DB) endpoint นี้ต้องคืนลิสต์ว่าง
    /// ไม่ใช่ 500 · <c>JsonElement.TryGetProperty</c> **โยน** InvalidOperationException กับ root ที่
    /// เป็น array ไม่ได้คืน false เฉย ๆ จึงต้องเช็ค ValueKind ก่อน
    ///
    /// วันนี้ยังไม่มีทางให้ผู้ใช้เขียน sheet ดิบ ๆ (ตรวจแล้ว: ทุกจุดที่เขียน sheet เป็น server เขียนเอง)
    /// เทสต์นี้จึงเขียนลง DB ตรง ๆ เพื่อจำลองสถานะที่ยังไปไม่ถึงผ่าน API — กันไว้ที่ขอบเขตความเชื่อถือ
    /// </summary>
    [Fact]
    public async Task Character_with_a_non_object_sheet_returns_empty_options_not_500()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            "UPDATE characters SET sheet = '[1,2,3]'::jsonb WHERE id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", _heroCharacterId);
            await cmd.ExecuteNonQueryAsync();
        }

        var body = await GetAffordancesAsync(_playerToken, _heroCharacterId);

        Assert.Empty(body.Options);
    }

    // ── สิทธิ์ ────────────────────────────────────────────────────────────────

    /// <summary>ผู้เล่นขอ affordance ของตัวละครคนอื่นไม่ได้ — ไม่งั้นจะกลายเป็นช่องส่องว่าคนอื่น
    /// มองเห็นอะไรบ้าง ซึ่งรั่วยิ่งกว่าเดิม</summary>
    [Fact]
    public async Task Player_requesting_another_players_character_gets_403()
    {
        var response = await AuthedClient(_playerToken).GetAsync(AffordanceUrl(_otherPlayersCharacterId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>คนนอกแคมเปญต้องได้ 404 (ไม่ใช่ 403) — ตอบ 403 เท่ากับยืนยันว่าแคมเปญ/ตัวละครนี้
    /// มีอยู่จริง ซึ่งเป็นการรั่วอีกแบบ · แพทเทิร์นเดียวกับ endpoint อื่นทั้งโปรเจกต์</summary>
    [Fact]
    public async Task Outsider_gets_404()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync(AffordanceUrl(_heroCharacterId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(AffordanceUrl(_heroCharacterId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>DM ขอ affordance ของ NPC ที่ตัวเองคุมได้ (ControlledByDm) — ใช้ helper
    /// <c>RequireCharacterActorAsync</c> เดิม ไม่เขียนเช็คสิทธิ์ใหม่</summary>
    [Fact]
    public async Task Dm_can_request_affordances_for_an_npc_they_control()
    {
        var response = await AuthedClient(_dmToken).GetAsync(AffordanceUrl(_visibleGoblinCharacterId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string AffordanceUrl(Guid characterId) =>
        $"/campaigns/{_campaignId}/sessions/{_sessionId}/affordances/{characterId}";

    private async Task<AffordanceResponse> GetAffordancesAsync(string token, Guid characterId)
    {
        var response = await AuthedClient(token).GetAsync(AffordanceUrl(characterId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AffordanceResponse>(JsonOptions))!;
    }

    private static async Task<Guid> LookupCatalogIdAsync(NpgsqlConnection conn, string key)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM action_catalog_entries WHERE key = @key AND rules_system = 'srd2024'", conn);
        cmd.Parameters.AddWithValue("key", key);
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (Guid)result!;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"affordance-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Affordance {label}",
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
        NpgsqlConnection conn, Guid id, string name, Guid? ownerUserId, bool controlledByDm, string sheetJson)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, @sheet::jsonb, 20, 20, 0, 12, 0,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @controlledByDm, false,
                 false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("sheet", sheetJson);
        cmd.Parameters.AddWithValue("controlledByDm", controlledByDm);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertSceneAsync(
        NpgsqlConnection conn, Guid id, Guid sessionId, string tokensJson)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@id, @sessionId, 'Affordance Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"22222222-2222-2222-2222-222222222222","x":0,"y":0,"radius":10}]}',
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
                "ids", new[] { _dmUserId, _playerUserId, _otherPlayerUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }

        _factory.Dispose();
    }

    // ── response contracts (นิยามในเทสต์เอง ไม่พึ่ง internal type ของ Api) ──

    private record AuthTokenResponse(string Token, Guid UserId);

    private record AffordanceNoteResponse(string Code, string Message);

    private record AffordanceItemResponse(
        Guid OptionId,
        string OptionName,
        Guid? TargetId,
        string? TargetName,
        string AdvantageMode,
        List<AffordanceNoteResponse> Notes);

    private record AffordanceResponse(Guid CharacterId, List<AffordanceItemResponse> Options);
}

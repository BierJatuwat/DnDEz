using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 7 — token ที่ผูกกับ monster stat block ต้องเข้าสู่รายการเป้าหมายของ affordance
/// (spatial-play-design §2.4: อาวุธ/เวท/action ของมอนสเตอร์เป็นของอย่างเดียวกันในสายตา engine)
///
/// ก่อน Part นี้ `BuildVisibleTargetsAsync` ข้าม token ที่ไม่ใช่ Character ไปเงียบ ๆ — มอนสเตอร์จึง
/// ไม่มีตัวตนในระบบเป้าหมายเลย
///
/// ⚠️ **เพิ่มแหล่งข้อมูลใหม่เข้าไปในรายการ "ทำอะไรได้บ้าง" = เพิ่มทางรั่วใหม่** ว่า "มีมอนสเตอร์อะไรอยู่
/// ตรงไหน" (§1.2) — เทสต์ adversarial ด้านล่างจึงตรวจ raw JSON แบบเดียวกับ Sprint G Part 9
///
/// ฉาก: หมอกเปิดวง (0,0) รัศมี 10 · ฮีโร่ (1,1) ถือดาบยาว · ก็อบลินหอก (2,2) เห็นได้ ห่าง 5 ฟุต
/// · ผีในเงามืด (99,99) อยู่นอกหมอก
/// </summary>
public class MonsterTargetAffordanceTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "monster-target-secret-1";
    private const string HiddenMonsterName = "ผีในเงามืด";
    private const string VisibleMonsterName = "ก็อบลินหอก";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _visibleMonsterId = Guid.NewGuid();
    private readonly Guid _hiddenMonsterId = Guid.NewGuid();
    private readonly Guid _defaultsMonsterId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _longswordId;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        _longswordId = await LookupCatalogIdAsync(conn, "longsword");

        await ExecAsync(conn,
            """
            INSERT INTO campaigns (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@c, @dm, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now());
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (gen_random_uuid(), @c, @dm, 'Dm', now()), (gen_random_uuid(), @c, @player, 'Player', now());
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@s, @c, 'Online', 'Active', NULL, now(), NULL);
            """,
            ("c", _campaignId), ("dm", _dmUserId), ("player", _playerUserId), ("s", _sessionId),
            ("name", $"Monster Target {_suffix}"));

        await InsertMonsterAsync(conn, _visibleMonsterId, VisibleMonsterName, reachFeet: 5);
        await InsertMonsterAsync(conn, _hiddenMonsterId, HiddenMonsterName, reachFeet: 5);

        // มอนสเตอร์ที่ import มาโดยไม่ระบุ speed/reach — ต้องได้ค่าดีฟอลต์ ไม่ใช่พัง
        await ExecAsync(conn,
            """
            INSERT INTO monster_stat_blocks (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status)
            VALUES (@id, @c, 'มอนสเตอร์นำเข้าเก่า', 12, 7, '[]'::jsonb, '[]'::jsonb, 'Approved')
            """,
            ("id", _defaultsMonsterId), ("c", _campaignId));

        var heroSheet = $$"""{"class":"Fighter","actionCatalogRefs":["{{_longswordId}}"]}""";
        await ExecAsync(conn,
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at, strength, dexterity, constitution, intelligence, wisdom, charisma)
            VALUES (@hero, @c, @player, 'ฮีโร่', @sheet::jsonb, 20, 20, 0, 12, 0, 30,
                    ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, false, now(),
                    15, 13, 14, 12, 10, 8);
            """,
            ("hero", _heroId), ("c", _campaignId), ("player", _playerUserId), ("sheet", heroSheet));

        var tokens = $$"""
            [
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_heroId}}","x":1,"y":1},
                {"id":"{{Guid.NewGuid()}}","entityType":"Monster","entityId":"{{_visibleMonsterId}}","x":2,"y":2},
                {"id":"{{Guid.NewGuid()}}","entityType":"Monster","entityId":"{{_hiddenMonsterId}}","x":99,"y":99}
            ]
            """;
        await ExecAsync(conn,
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@sc, @s, 'Monster Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"88888888-8888-8888-8888-888888888888","x":0,"y":0,"radius":10}]}', true)
            """,
            ("sc", _sceneId), ("s", _sessionId), ("tokens", tokens));
    }

    // ── มอนสเตอร์เป็นเป้าได้จริง ─────────────────────────────────────────────

    /// <summary>หัวใจของ Part นี้ — ก่อนหน้านี้มอนสเตอร์ไม่โผล่ในรายการเลย ผู้เล่นจึง "โจมตีไม่ได้"</summary>
    [Fact]
    public async Task Visible_monster_is_offered_as_a_target()
    {
        var body = await AffordancesAsync(_playerToken, _heroId);

        var option = Assert.Single(body.Options.Where(o => o.TargetId == _visibleMonsterId));
        Assert.Equal(_longswordId, option.OptionId);
        Assert.Equal(VisibleMonsterName, option.TargetName);
        Assert.Equal(5, option.DistanceFeet);
    }

    /// <summary>
    /// ชนิดของเป้าติดไปด้วย เพราะ endpoint โจมตี (`POST .../attacks`) รับได้เฉพาะ **ตัวละคร** —
    /// มอนสเตอร์ยังไม่มีพลังชีวิตรายตัว (monster_stat_blocks เป็นแม่แบบระดับแคมเปญ ไม่ใช่ instance)
    /// หน้าจอต้องรู้ว่าเป้านี้ระบบยังทอยให้ไม่ได้ แทนที่จะยิงไปแล้วเด้ง error
    /// </summary>
    [Fact]
    public async Task Target_carries_its_entity_type()
    {
        var body = await AffordancesAsync(_playerToken, _heroId);

        var monster = Assert.Single(body.Options.Where(o => o.TargetId == _visibleMonsterId));
        Assert.Equal("Monster", monster.TargetEntityType);
    }

    // ── ความปลอดภัย: แหล่งข้อมูลใหม่ต้องไม่เปิดทางรั่วใหม่ ────────────────────

    /// <summary>
    /// **เทสต์ adversarial ที่ dispatch สั่งให้มีโดยเฉพาะ** — ยิง endpoint ด้วย token ผู้เล่น แล้วตรวจ
    /// raw JSON ว่าไม่มีร่องรอยมอนสเตอร์ที่ยังอยู่ในหมอกเลยแม้แต่ตัวอักษรเดียว (ทั้ง id และชื่อ)
    /// </summary>
    [Fact]
    public async Task Player_response_raw_json_contains_no_trace_of_monsters_hidden_by_fog()
    {
        var response = await AuthedClient(_playerToken).GetAsync(AffordanceUrl(_heroId));
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(_hiddenMonsterId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HiddenMonsterName, raw, StringComparison.Ordinal);
        // เทสต์ต้องไม่ผ่านเพราะฉากว่าง — ตัวที่เห็นได้ต้องอยู่จริง
        Assert.Contains(VisibleMonsterName, raw, StringComparison.Ordinal);
    }

    /// <summary>ยืนยันว่ามอนสเตอร์ที่ซ่อนอยู่ **มีจริงในฉาก** ไม่ใช่เทสต์ข้างบนผ่านเพราะไม่มีมัน</summary>
    [Fact]
    public async Task Hidden_monster_really_exists_for_the_dm()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains(_hiddenMonsterId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── speed/reach ของมอนสเตอร์ ─────────────────────────────────────────────

    /// <summary>มอนสเตอร์ที่นำเข้ามาก่อน Part นี้ (ไม่ได้ระบุ speed/reach) ต้องได้ค่าดีฟอลต์ที่ใช้ได้จริง
    /// ไม่ใช่ 0 — 30 ฟุต/5 ฟุต คือค่ามาตรฐานของสิ่งมีชีวิตขนาดกลางตาม SRD</summary>
    [Fact]
    public async Task Monster_imported_without_speed_or_reach_gets_usable_defaults()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT speed_feet, reach_feet FROM monster_stat_blocks WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _defaultsMonsterId);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(30, reader.GetInt32(0));
        Assert.Equal(5, reader.GetInt32(1));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string AffordanceUrl(Guid characterId) =>
        $"/campaigns/{_campaignId}/sessions/{_sessionId}/affordances/{characterId}";

    private async Task<AffordanceResponse> AffordancesAsync(string token, Guid characterId)
    {
        var response = await AuthedClient(token).GetAsync(AffordanceUrl(characterId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AffordanceResponse>(JsonOptions))!;
    }

    private async Task InsertMonsterAsync(NpgsqlConnection conn, Guid id, string name, int reachFeet) =>
        await ExecAsync(conn,
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status, speed_feet, reach_feet)
            VALUES (@id, @c, @name, 13, 11, '[]'::jsonb, '[]'::jsonb, 'Approved', 30, @reach)
            """,
            ("id", id), ("c", _campaignId), ("name", name), ("reach", reachFeet));

    private static async Task<Guid> LookupCatalogIdAsync(NpgsqlConnection conn, string key)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM action_catalog_entries WHERE key = @key AND rules_system = 'srd2024'", conn);
        cmd.Parameters.AddWithValue("key", key);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"monster-target-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Monster Target {label}",
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
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = @c", ("c", _campaignId));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", new[] { _dmUserId, _playerUserId }));
        _factory.Dispose();
    }

    private record AuthTokenResponse(string Token, Guid UserId);

    private record AffordanceItemResponse(
        Guid OptionId, string OptionName, Guid? TargetId, string? TargetName, string? TargetEntityType, int? DistanceFeet);

    private record AffordanceResponse(Guid CharacterId, List<AffordanceItemResponse> Options);
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 7 — มอนสเตอร์เป็น "ภัยคุกคาม" ได้จริง และ **ระยะเอื้อมมาจาก stat block ไม่ใช่ 5 ฟุตตายตัว**
///
/// ก่อน Part นี้ `MovementSceneContext` กรองเฉพาะ token ที่เป็น Character และใช้ `DefaultReachFeet = 5`
/// กับทุกตัว → มอนสเตอร์ทำ OA ไม่ได้เลย และมอนสเตอร์เอื้อม 10 ฟุตก็ถูกคิดเป็น 5
/// engine รองรับ reach อื่นอยู่แล้ว (OpportunityAttackResolverTests มีเคส 10 ฟุต) — ขาดแค่ข้อมูล
///
/// ฉาก: ฮีโร่ (0,0) speed 30 · มอนสเตอร์เอื้อมยาว (2,0) = ห่าง 10 ฟุต · หมอกเปิดกว้าง
/// เดินขึ้นเหนือ (0,1) → (0,2) → (0,3): ระยะจากมอนสเตอร์ = 10, 10, 15 ฟุต → หลุดเอื้อมตอนถึง (0,3)
/// </summary>
public class MonsterThreatMovementTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "monster-threat-secret-1";
    private const string LongReachMonsterName = "ออร์คหอกยาว";
    private const string HiddenMonsterName = "หนอนซุ่มใต้ดิน";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _heroTokenId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _longReachMonsterId = Guid.NewGuid();
    private readonly Guid _hiddenMonsterId = Guid.NewGuid();

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
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES (@hero, @c, @player, 'ฮีโร่', '{}'::jsonb, 20, 20, 0, 12, 0, 30,
                    ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, false, now());
            """,
            ("c", _campaignId), ("dm", _dmUserId), ("player", _playerUserId), ("s", _sessionId),
            ("hero", _heroId), ("name", $"Monster Threat {_suffix}"));

        await InsertMonsterAsync(conn, _longReachMonsterId, LongReachMonsterName, reachFeet: 10);
        await InsertMonsterAsync(conn, _hiddenMonsterId, HiddenMonsterName, reachFeet: 10);

        await InsertSceneAsync(conn, fogRadius: 20);
    }

    // ── DoD: reach จาก stat block มีผลจริง ───────────────────────────────────

    /// <summary>
    /// **DoD ข้อแรก** — มอนสเตอร์เอื้อม 10 ฟุตต้องได้ OA ตอนผู้เล่นเดินออกจากระยะ 10 ฟุต
    /// ถ้าระบบยังคิด 5 ฟุตตายตัว ฮีโร่จะอยู่นอกเอื้อมตั้งแต่ต้น → ไม่มีอะไรหยุดการเดินเลย
    /// </summary>
    [Fact]
    public async Task Monster_with_ten_foot_reach_gets_an_opportunity_attack()
    {
        var body = await CommitAsync(_playerToken, [(0, 0), (0, 1), (0, 2), (0, 3)]);

        Assert.Equal("AwaitingReaction", body.Status);
        Assert.Equal(_longReachMonsterId, body.AwaitingThreatId);
        Assert.Equal(LongReachMonsterName, body.AwaitingThreatName);
        Assert.NotNull(body.PendingReactionId);
        // หยุดที่ช่องสุดท้ายที่ยังอยู่ในเอื้อม (การโจมตีเกิดจากช่องนั้น)
        Assert.Equal((0, 2), await TokenPositionAsync());
    }

    /// <summary>
    /// เอื้อม 5 ฟุตด้วยเส้นทางเดียวกันต้องไม่มี OA เลย — ฮีโร่อยู่ห่าง 10 ฟุตตั้งแต่แรก ไม่เคยอยู่ในเอื้อม
    /// (คู่กับเทสต์บน: พิสูจน์ว่าเลข reach มาจาก stat block จริง ไม่ใช่ค่าคงที่ที่บังเอิญถูก)
    /// </summary>
    [Fact]
    public async Task Same_path_against_a_five_foot_reach_monster_completes_without_interruption()
    {
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, "UPDATE monster_stat_blocks SET reach_feet = 5 WHERE id = @id",
                ("id", _longReachMonsterId));
        }

        var body = await CommitAsync(_playerToken, [(0, 0), (0, 1), (0, 2), (0, 3)]);

        Assert.Equal("Completed", body.Status);
        Assert.Null(body.PendingReactionId);
        Assert.Equal((0, 3), await TokenPositionAsync());
    }

    /// <summary>preview ต้องเตือนล่วงหน้าด้วยชื่อมอนสเตอร์ ก่อนผู้เล่นกดเดินจริง (§3.4)</summary>
    [Fact]
    public async Task Preview_warns_about_the_monster_before_committing()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/preview",
            new { CharacterId = _heroId, Path = Path([(0, 0), (0, 1), (0, 2), (0, 3)]), Disengage = false });
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<PreviewResponse>(JsonOptions))!;

        var warning = Assert.Single(body.Warnings);
        Assert.Equal(LongReachMonsterName, warning.ThreatName);
    }

    /// <summary>สิทธิ์ตอบ OA ของมอนสเตอร์เป็นของ DM (มอนสเตอร์ไม่มีเจ้าของ) — ผู้เล่นตอบแทนไม่ได้</summary>
    [Fact]
    public async Task The_dm_holds_the_reaction_rights_for_a_monster()
    {
        var pending = await CommitAsync(_playerToken, [(0, 0), (0, 1), (0, 2), (0, 3)]);
        var url = $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/reactions/{pending.PendingReactionId}/respond";

        var byPlayer = await AuthedClient(_playerToken).PostAsJsonAsync(url, new { UseReaction = false });
        Assert.Equal(HttpStatusCode.Forbidden, byPlayer.StatusCode);

        var byDm = await AuthedClient(_dmToken).PostAsJsonAsync(url, new { UseReaction = true });
        Assert.Equal(HttpStatusCode.OK, byDm.StatusCode);
        var resumed = (await byDm.Content.ReadFromJsonAsync<CommitResponse>(JsonOptions))!;
        Assert.True(resumed.ReactionUsed);
        Assert.Equal((0, 3), await TokenPositionAsync());
    }

    // ── ความปลอดภัย: มอนสเตอร์ในหมอกต้องไม่รั่วผ่านคำเตือน ────────────────────

    /// <summary>
    /// adversarial — มอนสเตอร์ที่ยังอยู่ในหมอกต้องไม่โผล่ในคำเตือนของ preview เลย (ทั้งชื่อและ id)
    /// ไม่งั้นคำเตือนจะกลายเป็นเครื่องตรวจจับว่ามีอะไรซุ่มอยู่ตรงไหน ซึ่งแรงกว่าการรู้ตัวเลขเสียอีก (§1.2)
    /// </summary>
    [Fact]
    public async Task Preview_says_nothing_about_a_monster_that_is_still_in_the_fog()
    {
        // หมอกแคบลงจนเห็นแค่รอบตัวฮีโร่ · หนอนซุ่มอยู่ที่ (1,1) ซึ่งนอกวง
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, "DELETE FROM scenes WHERE id = @id", ("id", _sceneId));
            await InsertSceneAsync(conn, fogRadius: 0.5);
        }

        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/preview",
            new { CharacterId = _heroId, Path = Path([(0, 0), (0, 1), (0, 2)]), Disengage = false });
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(HiddenMonsterName, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(_hiddenMonsterId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static object[] Path((int X, int Y)[] path) =>
        path.Select(p => (object)new { X = (double)p.X, Y = (double)p.Y }).ToArray();

    private async Task<CommitResponse> CommitAsync(string token, (int X, int Y)[] path)
    {
        var response = await AuthedClient(token).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/commit",
            new { CharacterId = _heroId, Path = Path(path), Disengage = false });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommitResponse>(JsonOptions))!;
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

    private async Task InsertMonsterAsync(NpgsqlConnection conn, Guid id, string name, int reachFeet) =>
        await ExecAsync(conn,
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status, speed_feet, reach_feet)
            VALUES (@id, @c, @name, 13, 15, '[]'::jsonb, '[]'::jsonb, 'Approved', 30, @reach)
            """,
            ("id", id), ("c", _campaignId), ("name", name), ("reach", reachFeet));

    private async Task InsertSceneAsync(NpgsqlConnection conn, double fogRadius)
    {
        var tokens = $$"""
            [
                {"id":"{{_heroTokenId}}","entityType":"Character","entityId":"{{_heroId}}","x":0,"y":0},
                {"id":"{{Guid.NewGuid()}}","entityType":"Monster","entityId":"{{_longReachMonsterId}}","x":2,"y":0},
                {"id":"{{Guid.NewGuid()}}","entityType":"Monster","entityId":"{{_hiddenMonsterId}}","x":1,"y":1}
            ]
            """;
        var fog = $$"""
            {"revealedAreas":[{"id":"99999999-9999-9999-9999-999999999999","x":0,"y":0,"radius":{{fogRadius}}}]}
            """;
        await ExecAsync(conn,
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@sc, @s, 'Monster Threat Scene', '{}', @tokens::jsonb, @fog::jsonb, true)
            """,
            ("sc", _sceneId), ("s", _sessionId), ("tokens", tokens), ("fog", fog));
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"monster-threat-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Monster Threat {label}",
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

    private record CommitResponse(
        Guid CharacterId, string Status, int CostFeet, Guid? PendingReactionId,
        Guid? AwaitingThreatId, string? AwaitingThreatName, int? ReactionTimeoutSeconds, bool ReactionUsed);

    private record WarningResponse(Guid ThreatId, string ThreatName, string Message);

    private record PreviewResponse(Guid CharacterId, int CostFeet, bool IsAffordable, List<WarningResponse> Warnings);
}

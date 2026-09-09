using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 3 — GET scene-prep: monster_stat_blocks + npc_profiles ของ beat ที่ระบุ
/// (design doc §13.2 — DM "peek" beat อื่นได้โดยไม่เปลี่ยน active beat จริง, §4.1 ว่าทำไม review
/// สำคัญขึ้นเมื่อมี affordance panel)
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// GET /campaigns/{cid}/beats/{bid}/scene-prep    [DM เท่านั้น]
///     200 + { BeatId, Monsters: [...], Npcs: [...] } — ตาม monster_refs/npc_refs ของ beat นั้น
///     ref ที่ไม่มีแถวจริงในตาราง (dangling) ถูกข้ามเงียบ ๆ ไม่ error
///     **คืนทุก ReviewStatus** (Pending/Approved/Rejected) — endpoint นี้เป็นจอ DM เอง ไม่ใช่
///     orchestrator context (กฎ "review_status != approved ห้ามเข้า orchestrator" ใน
///     PROJECT-CONTEXT §2 คุมคนละ pipeline — DM ต้องเห็นของที่ยังไม่ approve ถึงจะรีวิวได้)
///     403 = สมาชิกที่ไม่ใช่ DM · 404 = ไม่ใช่สมาชิก / beat ไม่อยู่ในแคมเปญนี้
/// </summary>
public class ScenePrepEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "scene-prep-endpoints-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();

    private readonly Guid _beatId = Guid.NewGuid();
    private readonly Guid _emptyBeatId = Guid.NewGuid();          // beat ไม่มี monster/npc ref เลย
    private readonly Guid _danglingBeatId = Guid.NewGuid();       // beat ref ไป id ที่ไม่มีแถวจริง
    private readonly Guid _otherCampaignBeatId = Guid.NewGuid();

    private readonly Guid _monsterId = Guid.NewGuid();
    private readonly Guid _pendingMonsterId = Guid.NewGuid();     // review_status = Pending
    private readonly Guid _npcId = Guid.NewGuid();

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

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Scene Prep Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _outsiderUserId, $"Other Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _outsiderUserId, "Dm");

        await InsertMonsterAsync(conn, _monsterId, _campaignId, "Goblin Boss", 17, 21, "Approved");
        await InsertMonsterAsync(conn, _pendingMonsterId, _campaignId, "Unreviewed Ogre", 11, 59, "Pending");
        await InsertNpcAsync(conn, _npcId, _campaignId, "Old Sailor",
            knowsSecrets: """["the captain is a doppelganger"]""");

        await InsertBeatAsync(conn, _beatId, _campaignId, 0, [_monsterId, _pendingMonsterId], [_npcId]);
        await InsertBeatAsync(conn, _emptyBeatId, _campaignId, 1, [], []);
        await InsertBeatAsync(conn, _danglingBeatId, _campaignId, 2, [Guid.NewGuid()], [Guid.NewGuid()]);
        await InsertBeatAsync(conn, _otherCampaignBeatId, _otherCampaignId, 0, [], []);
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _otherCampaignId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }
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

    private static async Task InsertMonsterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, int ac, int hp, string reviewStatus)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status)
            VALUES (@id, @campaignId, @name, @ac, @hp, '[]', '[]', @reviewStatus)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("ac", ac);
        cmd.Parameters.AddWithValue("hp", hp);
        cmd.Parameters.AddWithValue("reviewStatus", reviewStatus);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertNpcAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, string knowsSecrets)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO npc_profiles (id, campaign_id, name, knows_secrets, review_status)
            VALUES (@id, @campaignId, @name, @knowsSecrets::jsonb, 'Approved')
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("knowsSecrets", knowsSecrets);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertBeatAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, int order, Guid[] monsterRefs, Guid[] npcRefs)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO story_beats
                (id, campaign_id, "order", title, exit_conditions, monster_refs, npc_refs, secrets,
                 progress, review_status)
            VALUES
                (@id, @campaignId, @order, 'Beat', ARRAY[]::text[], @monsterRefs, @npcRefs, '[]',
                 'Upcoming', 'Approved')
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("order", order);
        cmd.Parameters.AddWithValue("monsterRefs", monsterRefs);
        cmd.Parameters.AddWithValue("npcRefs", npcRefs);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"sceneprep-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"ScenePrep {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private record AuthResponse(string Token, Guid UserId);

    private record MonsterResponse(
        Guid Id, string Name, int ArmorClass, int HitPoints, string Actions, string ConditionRefs, string ReviewStatus);

    private record NpcResponse(
        Guid Id, string Name, string? Motive, string? VoiceHint, string KnowsSecrets, string ReviewStatus);

    private record ScenePrepResponse(Guid BeatId, List<MonsterResponse> Monsters, List<NpcResponse> Npcs);

    // ── happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_gets_monsters_and_npcs_for_beat_including_unreviewed()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/beats/{_beatId}/scene-prep");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ScenePrepResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(_beatId, body!.BeatId);

        Assert.Equal(2, body.Monsters.Count);
        Assert.Contains(body.Monsters, m => m.Id == _monsterId && m.Name == "Goblin Boss" && m.ReviewStatus == "Approved");
        // DM ต้องเห็น monster ที่ยังไม่ approve ด้วย — จอนี้เป็นจอรีวิวของ DM เอง ไม่ใช่ orchestrator context
        Assert.Contains(body.Monsters, m => m.Id == _pendingMonsterId && m.ReviewStatus == "Pending");

        Assert.Single(body.Npcs);
        Assert.Equal(_npcId, body.Npcs[0].Id);
        Assert.Contains("doppelganger", body.Npcs[0].KnowsSecrets);
    }

    [Fact]
    public async Task Beat_with_no_refs_returns_empty_lists_not_404()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/beats/{_emptyBeatId}/scene-prep");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ScenePrepResponse>(JsonOptions);
        Assert.Empty(body!.Monsters);
        Assert.Empty(body.Npcs);
    }

    [Fact]
    public async Task Dangling_refs_are_skipped_silently_not_errored()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/beats/{_danglingBeatId}/scene-prep");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ScenePrepResponse>(JsonOptions);
        Assert.Empty(body!.Monsters);
        Assert.Empty(body.Npcs);
    }

    // ── auth ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Player_gets_403()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/beats/{_beatId}/scene-prep");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_gets_404_not_403()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/beats/{_beatId}/scene-prep");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Beat_from_another_campaign_returns_404()
    {
        // DM ของ _campaignId เรียกด้วย beatId ที่จริง ๆ อยู่แคมเปญอื่น — ต้อง 404 เหมือนไม่มีอยู่จริง
        var response = await AuthedClient(_dmToken).GetAsync(
            $"/campaigns/{_campaignId}/beats/{_otherCampaignBeatId}/scene-prep");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── ไม่หลุด secrets ไปทาง endpoint ที่ผู้เล่นเรียกได้ ─────────────────────
    // (บทเรียน Sprint F งานที่ 7: ยิงจริงด้วย token ผู้เล่น ตรวจ raw JSON ไม่ใช่แค่เชื่อว่า UI ไม่แสดง
    // endpoint นี้ทั้งตัวเป็น DM-only จึงพิสูจน์ด้วยการยืนยันว่า response ของผู้เล่นไม่มีเนื้อหาเลย
    // ไม่ใช่แค่ status code ผิด — กันเคสที่ 403 handler เผลอ serialize body ติดมาด้วย)
    [Fact]
    public async Task Player_response_body_contains_no_secret_fields()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/beats/{_beatId}/scene-prep");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("knowsSecrets", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("doppelganger", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("armorClass", raw, StringComparison.OrdinalIgnoreCase);
    }
}

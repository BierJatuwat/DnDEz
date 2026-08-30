using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 2a — dial autonomy ต่อ subsystem
///
/// contract:
///
/// PUT /campaigns/{campaignId}/autonomy-settings/{subsystem}  [Authorize, DM เท่านั้น]
///   subsystem ใน route = ชื่อ AutonomySubsystem (Narration|NpcDialogue|RulesCombat|MapScene, ไม่สนตัวพิมพ์)
///   body { Level: string }  (Manual|Suggest|Autonomous, ไม่สนตัวพิมพ์)
///   200 OK + { CampaignId, Subsystem, Level } — upsert ทับแถวเดิมของ (campaign, subsystem)
///   400 subsystem/level ไม่ถูกต้อง · 403 ไม่ใช่ DM · 404 แคมเปญมองไม่เห็น/ไม่มีอยู่จริง
///   409 แพ้ race INSERT พร้อมกัน (ยิงซ้ำได้ idempotent)
///
/// GET /campaigns/{campaignId}/autonomy-settings  [Authorize, สมาชิกทุกคน]
///   200 OK + [{ CampaignId, Subsystem, Level }] ครบทั้ง 4 subsystem เสมอ เรียง Narration,
///   NpcDialogue, RulesCombat, MapScene — subsystem ที่ยังไม่เคยตั้งค่าคืน default "Suggest"
///   404 ไม่ใช่สมาชิก/แคมเปญไม่มีอยู่จริง
///
/// ⚠️ จุดที่เทสต์นี้ต้องเฝ้าเป็นพิเศษ: DM-only ของ PUT **บังคับสองชั้นอิสระต่อกัน** — เช็ค
/// `OwnerUserId` ในชั้น C# กับ policy `dm_write` ในชั้น DB · เทสต์ไฟล์นี้ยิงผ่าน HTTP จึงล็อก
/// เฉพาะสัญญาของชั้น C# (status code, ลำดับ 404-ก่อน-403) ส่วนการพิสูจน์ชั้น DB ล้วน ๆ อยู่ใน
/// <c>AutonomySettingRlsTests.cs</c> — ถ้าใครถอดเช็คใน C# ออกโดยคิดว่า RLS รับไว้ให้แล้ว
/// เทสต์ `Player_cannot_set_*` ในไฟล์นี้คือด่านสุดท้ายที่จะจับได้
/// </summary>
public class AutonomySettingEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "autonomy-setting-endpoint-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();

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

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Autonomy Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
    }

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name)
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

    /// <summary>อ่านค่าดิบจากตารางตรง ๆ ด้วย owner creds (ข้าม RLS) — พิสูจน์ว่า persist จริง
    /// ไม่ใช่แค่ echo กลับมาใน response</summary>
    private async Task<string?> ReadLevelDirectlyAsync(Guid campaignId, string subsystem)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT level FROM autonomy_settings WHERE campaign_id = @campaignId AND subsystem = @subsystem",
            conn);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("subsystem", subsystem);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private async Task<long> CountRowsAsync(Guid campaignId, string subsystem)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM autonomy_settings WHERE campaign_id = @campaignId AND subsystem = @subsystem",
            conn);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("subsystem", subsystem);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = @id", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("id", _campaignId);
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        var userIds = new[] { _dmUserId, _playerUserId, _outsiderUserId }
            .Where(id => id != Guid.Empty).ToArray();
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", userIds);
            await deleteUsers.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"autonomy-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Autonomy {label}",
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

    private Task<HttpResponseMessage> PutLevelAsync(string token, Guid campaignId, string subsystem, string? level) =>
        AuthedClient(token).PutAsJsonAsync(
            $"/campaigns/{campaignId}/autonomy-settings/{subsystem}", new { Level = level });

    // ── set level (DM-only) ──────────────────────────────────────────────

    [Fact]
    public async Task Dm_sets_a_subsystem_level_and_it_is_persisted()
    {
        var response = await PutLevelAsync(_dmToken, _campaignId, "Narration", "Autonomous");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AutonomySettingResponse>(JsonOptions);
        Assert.Equal(_campaignId, body!.CampaignId);
        Assert.Equal("Narration", body.Subsystem);
        Assert.Equal("Autonomous", body.Level);

        Assert.Equal("Autonomous", await ReadLevelDirectlyAsync(_campaignId, "Narration"));
    }

    [Fact]
    public async Task Dm_updating_same_subsystem_twice_replaces_value_without_duplicating_row()
    {
        // regression ที่สำคัญที่สุดของงานนี้: upsert ต้องทับแถวเดิม ไม่ใช่ INSERT ซ้ำจนชน
        // unique index ix_autonomy_settings_campaign_id_subsystem แล้วกลายเป็น 500
        var first = await PutLevelAsync(_dmToken, _campaignId, "RulesCombat", "Manual");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Manual", await ReadLevelDirectlyAsync(_campaignId, "RulesCombat"));

        var second = await PutLevelAsync(_dmToken, _campaignId, "RulesCombat", "Autonomous");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<AutonomySettingResponse>(JsonOptions);
        Assert.Equal("Autonomous", body!.Level);
        Assert.Equal("Autonomous", await ReadLevelDirectlyAsync(_campaignId, "RulesCombat"));
        Assert.Equal(1, await CountRowsAsync(_campaignId, "RulesCombat"));
    }

    [Fact]
    public async Task Setting_same_level_repeatedly_is_idempotent()
    {
        await PutLevelAsync(_dmToken, _campaignId, "MapScene", "Manual");
        var repeat = await PutLevelAsync(_dmToken, _campaignId, "MapScene", "Manual");

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal("Manual", await ReadLevelDirectlyAsync(_campaignId, "MapScene"));
        Assert.Equal(1, await CountRowsAsync(_campaignId, "MapScene"));
    }

    [Theory]
    [InlineData("narration", "Narration")]
    [InlineData("NPCDIALOGUE", "NpcDialogue")]
    [InlineData("rulesCombat", "RulesCombat")]
    public async Task Subsystem_in_route_is_case_insensitive_and_echoed_canonically(
        string routeValue, string canonical)
    {
        var response = await PutLevelAsync(_dmToken, _campaignId, routeValue, "manual");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AutonomySettingResponse>(JsonOptions);
        Assert.Equal(canonical, body!.Subsystem);
        Assert.Equal("Manual", body.Level); // level ก็ normalize เป็นชื่อ canonical เหมือนกัน
    }

    [Fact]
    public async Task Player_cannot_set_autonomy_level()
    {
        // เทสต์นี้ล็อกด่านชั้น C# (เช็ค OwnerUserId → 403) ส่วนชั้น DB มี policy `dm_write` กันซ้ำอีกชั้น
        var response = await PutLevelAsync(_playerToken, _campaignId, "Narration", "Autonomous");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await ReadLevelDirectlyAsync(_campaignId, "Narration"));
    }

    [Fact]
    public async Task Outsider_setting_level_gets_404_not_403()
    {
        // คนนอกต้องไม่ได้รับการยืนยันว่าแคมเปญนี้มีอยู่จริง (convention เดียวกับทั้งระบบ)
        var response = await PutLevelAsync(_outsiderToken, _campaignId, "Narration", "Autonomous");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Telepathy")]     // ชื่อที่ไม่มีอยู่
    [InlineData("99")]            // ค่าตัวเลขที่ไม่มีนิยาม — Enum.TryParse ดิบ ๆ จะปล่อยผ่าน
    [InlineData("0")]             // รูปตัวเลขของ Narration — ไม่ใช่ contract ที่ประกาศไว้
    [InlineData("Narration,MapScene")] // comma-list — TryParse รับแม้ไม่ใช่ [Flags]
    public async Task Invalid_subsystem_in_route_returns_400(string invalidSubsystem)
    {
        var response = await PutLevelAsync(_dmToken, _campaignId, invalidSubsystem, "Manual");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Godmode")]
    [InlineData("99")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task Invalid_level_in_body_returns_400(string? invalidLevel)
    {
        var response = await PutLevelAsync(_dmToken, _campaignId, "Narration", invalidLevel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await ReadLevelDirectlyAsync(_campaignId, "Narration"));
    }

    [Fact]
    public async Task Set_level_on_nonexistent_campaign_returns_404()
    {
        var response = await PutLevelAsync(_dmToken, Guid.NewGuid(), "Narration", "Manual");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync(
            $"/campaigns/{_campaignId}/autonomy-settings/Narration", new { Level = "Manual" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── read levels (any member) ─────────────────────────────────────────

    [Fact]
    public async Task Get_before_anything_is_set_returns_all_four_subsystems_defaulting_to_suggest()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/autonomy-settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.Content.ReadFromJsonAsync<List<AutonomySettingResponse>>(JsonOptions);
        Assert.NotNull(settings);

        Assert.Equal(
            new[] { "Narration", "NpcDialogue", "RulesCombat", "MapScene" },
            settings!.Select(s => s.Subsystem).ToArray());
        Assert.All(settings, s => Assert.Equal("Suggest", s.Level));
        Assert.All(settings, s => Assert.Equal(_campaignId, s.CampaignId));
    }

    [Fact]
    public async Task Get_after_partial_set_shows_set_values_and_defaults_for_the_rest()
    {
        await PutLevelAsync(_dmToken, _campaignId, "Narration", "Autonomous");
        await PutLevelAsync(_dmToken, _campaignId, "MapScene", "Manual");

        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/autonomy-settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.Content.ReadFromJsonAsync<List<AutonomySettingResponse>>(JsonOptions);
        Assert.NotNull(settings);

        var bySubsystem = settings!.ToDictionary(s => s.Subsystem, s => s.Level);
        Assert.Equal("Autonomous", bySubsystem["Narration"]);
        Assert.Equal("Manual", bySubsystem["MapScene"]);
        Assert.Equal("Suggest", bySubsystem["NpcDialogue"]);   // ยังไม่เคยตั้ง = default
        Assert.Equal("Suggest", bySubsystem["RulesCombat"]);
    }

    [Fact]
    public async Task Player_member_can_read_levels_even_though_they_cannot_set_them()
    {
        await PutLevelAsync(_dmToken, _campaignId, "NpcDialogue", "Autonomous");

        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/autonomy-settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.Content.ReadFromJsonAsync<List<AutonomySettingResponse>>(JsonOptions);
        Assert.Contains(settings!, s => s.Subsystem == "NpcDialogue" && s.Level == "Autonomous");
    }

    [Fact]
    public async Task Non_member_cannot_read_levels()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/autonomy-settings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_on_nonexistent_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{Guid.NewGuid()}/autonomy-settings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record AutonomySettingResponse(Guid CampaignId, string Subsystem, string Level);
}

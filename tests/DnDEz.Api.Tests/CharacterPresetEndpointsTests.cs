using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 7 — คลัง preset ตัวละคร (design doc §5, player-side-design §18-19)
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// GET  /campaigns/{cid}/character-presets      [สมาชิกทุกคน — ไม่ใช่ DM-only]
///      200 + PresetListItem[] (Global ของระบบ + Campaign ของแคมเปญนี้เอง) · 404 ไม่ใช่สมาชิก
///
/// POST /campaigns/{cid}/characters             [PresetId มีค่า = สร้างจาก preset ทั้งหมด]
///      body { Name (required), PresetId } · 201 + HP/AC/ability score/speed จาก preset ตรง ๆ
///      ไม่ผสมกับ Level/MaxHp/... ที่อาจส่งมาพร้อมกัน (ถูกละเว้นทั้งหมดเมื่อมี PresetId)
///      400 = Name ขาด/ว่าง · 404 = preset ไม่มีอยู่จริง/มองไม่เห็น (Campaign ของแคมเปญอื่น)
///
/// **novice check ของ Part นี้**: เลือก preset "นักรบ" แล้วได้ HP/AC จริงทันทีในเนื้อ response เดียว
/// ไม่ต้องเรียก endpoint อื่นต่อ ไม่ต้องรู้ว่าดาบยาวทอย 1d8 (ดู
/// <see cref="Creating_character_from_fighter_preset_returns_real_stats_immediately"/>)
///
/// ใช้ preset จริงที่ seed มาจาก migration `SeedActionCatalogAdditionsAndCharacterPresets`
/// (id คงที่ '20000000-...-000000000001' = นักรบ) แทนการสร้าง fixture ของตัวเอง — เทสต์นี้จึงพิสูจน์
/// preset จริงที่ผู้ใช้จะเจอ ไม่ใช่ preset ปลอมที่ทำเทียบไว้
/// </summary>
public class CharacterPresetEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "character-preset-endpoints-secret-1";

    private static readonly Guid FighterPresetId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid WizardPresetId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _campaignScopedPresetId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _playerUserId, $"Preset Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _outsiderUserId, $"Other Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Dm");
        await InsertMembershipAsync(conn, _otherCampaignId, _outsiderUserId, "Dm");

        // preset ส่วนตัวของแคมเปญนี้เท่านั้น — ใช้ทดสอบว่าแคมเปญอื่นมองไม่เห็น
        await InsertCampaignScopedPresetAsync(conn, _campaignScopedPresetId, _campaignId);
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
            deleteUsers.Parameters.AddWithValue("ids", new[] { _playerUserId, _outsiderUserId });
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

    private async Task InsertCampaignScopedPresetAsync(NpgsqlConnection conn, Guid id, Guid campaignId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO character_presets
                (id, scope, campaign_id, source, name, build, level_path, level,
                 build_fingerprint, popularity_count, srd_only)
            VALUES (
                @id, 'Campaign', @campaignId, 'Authored', 'ทดสอบเฉพาะแคมเปญ',
                '{"race":"Human","class":"Fighter","abilityScores":{"strength":10,"dexterity":10,"constitution":10,"intelligence":10,"wisdom":10,"charisma":10},"speedFeet":30,"armorClass":10,"hitPoints":8,"equipment":[],"actionCatalogRefs":[]}'::jsonb,
                '{}'::jsonb, 1, @fingerprint, 1, false
            )
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("fingerprint", $"test-campaign-scoped-{_suffix}");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"preset-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Preset {label}",
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

    private record PresetAbilityScores(
        int Strength, int Dexterity, int Constitution, int Intelligence, int Wisdom, int Charisma);

    private record PresetListItem(
        Guid Id, string Name, string Scope, string Source, int Level, int PopularityCount,
        string? Race, string? Class, int? ArmorClass, int? HitPoints, PresetAbilityScores? AbilityScores);

    private record CharacterCreatedResponse(
        Guid Id, Guid CampaignId, string Name, int Level, Guid? OwnerUserId, bool ControlledByDm,
        bool NpcStatsVisibleToParty, int CurrentHp, int MaxHp, int CurrentTempHp, int ArmorClass,
        string[] DamageResistances, string[] DamageVulnerabilities, string[] DamageImmunities,
        int? Strength, int? Dexterity, int? Constitution, int? Intelligence, int? Wisdom, int? Charisma,
        int? SpeedFeet, string[]? Equipment);

    // ── GET list ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Player_sees_global_presets_including_fighter()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/character-presets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var presets = await response.Content.ReadFromJsonAsync<List<PresetListItem>>(JsonOptions);
        Assert.NotNull(presets);
        Assert.True(presets!.Count >= 6, "ต้องมี preset authored ที่ seed ไว้อย่างน้อย 6 ตัว");

        var fighter = presets.Single(p => p.Id == FighterPresetId);
        Assert.Equal("Global", fighter.Scope);
        Assert.Equal("Authored", fighter.Source);
        Assert.Equal("Fighter", fighter.Class);
        Assert.Equal(18, fighter.ArmorClass);
        Assert.Equal(12, fighter.HitPoints);
        Assert.NotNull(fighter.AbilityScores);
        Assert.Equal(15, fighter.AbilityScores!.Strength);
    }

    [Fact]
    public async Task Player_sees_own_campaign_scoped_preset()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/character-presets");
        var presets = await response.Content.ReadFromJsonAsync<List<PresetListItem>>(JsonOptions);

        Assert.Contains(presets!, p => p.Id == _campaignScopedPresetId);
    }

    [Fact]
    public async Task Outsider_does_not_see_other_campaigns_scoped_preset_but_still_sees_global()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_otherCampaignId}/character-presets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var presets = await response.Content.ReadFromJsonAsync<List<PresetListItem>>(JsonOptions);

        Assert.DoesNotContain(presets!, p => p.Id == _campaignScopedPresetId);
        Assert.Contains(presets!, p => p.Id == FighterPresetId);
    }

    [Fact]
    public async Task Non_member_gets_404()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/character-presets");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST create from preset — novice check ───────────────────────────

    [Fact]
    public async Task Creating_character_from_fighter_preset_returns_real_stats_immediately()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/characters", new { Name = "Aldric", PresetId = FighterPresetId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.NotNull(body);

        // novice check: HP/AC จริงทันที ไม่ต้องคำนวณ ไม่ต้องรู้กฎ — ตัวเลขต้องตรงกับ preset seed เป๊ะ
        Assert.Equal(12, body!.MaxHp);
        Assert.Equal(12, body.CurrentHp);
        Assert.Equal(18, body.ArmorClass);
        Assert.Equal(1, body.Level);
        Assert.Equal(15, body.Strength);
        Assert.Equal(30, body.SpeedFeet);
        Assert.Contains(body.Equipment!, e => e.Contains("Longsword"));
    }

    [Fact]
    public async Task Creating_character_from_wizard_preset_grants_both_spells()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/characters", new { Name = "Mira", PresetId = WizardPresetId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.Equal(7, body!.MaxHp);
        Assert.Equal(12, body.ArmorClass);
        Assert.Equal(15, body.Intelligence);

        // ยืนยันจาก Sheet โดยตรงว่า actionCatalogRefs ผูกทั้งสองเวทจริง (ไม่ใช่แค่ equipment string)
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT sheet::text FROM characters WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", body.Id);
        var sheet = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.Contains("86636c2c-1c6b-4488-a5f5-f3ea56f984ba", sheet); // fire-bolt
        Assert.Contains("2d864a4f-130f-4375-9063-70863b3d4774", sheet); // magic-missile
    }

    [Fact]
    public async Task Preset_stat_fields_in_request_body_are_ignored_in_favor_of_the_preset()
    {
        // ส่ง Level/MaxHp/ArmorClass มาพร้อม PresetId — ต้องถูกละเว้นทั้งหมด ค่าจาก preset ชนะเสมอ
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/characters",
            new { Name = "Override Test", PresetId = FighterPresetId, Level = 20, MaxHp = 999, ArmorClass = 1 });

        var body = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.Equal(1, body!.Level);
        Assert.Equal(12, body.MaxHp);
        Assert.Equal(18, body.ArmorClass);
    }

    [Fact]
    public async Task Creating_from_preset_increments_popularity_count()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        async Task<int> ReadPopularityAsync()
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT popularity_count FROM character_presets WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", FighterPresetId);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        var before = await ReadPopularityAsync();

        await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/characters", new { Name = "Popularity Test", PresetId = FighterPresetId });

        var after = await ReadPopularityAsync();
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Missing_name_with_preset_returns_400()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/characters", new { PresetId = FighterPresetId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Nonexistent_preset_returns_404()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/characters", new { Name = "Ghost", PresetId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Preset_scoped_to_another_campaign_is_not_usable_here()
    {
        // _campaignScopedPresetId เป็นของ _campaignId เท่านั้น — สมาชิกแคมเปญอื่นใช้ไม่ได้
        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            $"/campaigns/{_otherCampaignId}/characters",
            new { Name = "Cross Campaign", PresetId = _campaignScopedPresetId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

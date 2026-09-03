using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint A Part 3 — CRUD บาง ๆ ของ `characters` ภายใต้แคมเปญที่เลือกแล้ว (create + list + get)
/// ต่อ Postgres จริง ห้าม mock — seed แคมเปญ/สมาชิก/ตัวละครผ่าน owner role ตรง ๆ
///
/// contract ที่ implementation ต้องทำตาม:
///
/// POST /campaigns/{campaignId:guid}/characters  [Authorize]
///   body { Name (required), Level? } · default Level = 1
///   OwnerUserId มาจาก claim ของ JWT เท่านั้น (ClaimsPrincipalExtensions.GetUserId) **ห้ามรับจาก body**
///   ControlledByDm=false, HarvestConsent=false, IsOrphan=false เสมอ · Sheet = "{}"
///   201 Created + { Id, CampaignId, Name, Level, OwnerUserId }
///   400 เมื่อ Name ขาด/ว่าง/ยาวเกิน varchar(100) หรือ Level นอกช่วง 1–20
///     (ck_characters_level มีอยู่จริงที่ DB — ต้องดักที่ handler ก่อน ไม่งั้นทรานแซกชันของ
///      middleware ที่คลุมทั้ง request จะ abort แล้วกลายเป็น 500)
///   404 เมื่อไม่ใช่สมาชิกแคมเปญนั้น และต้องไม่มีแถวใหม่เกิดขึ้นเลย
///
/// GET /campaigns/{campaignId:guid}/characters  [Authorize]
///   200 + array ของ { Id, Name, Level, OwnerUserId, ControlledByDm } — **ห้ามส่ง Sheet ในลิสต์**
///   **ลำดับต้อง deterministic: Id ascending** · 404 เมื่อไม่ใช่สมาชิกแคมเปญนั้น
///
/// GET /campaigns/{campaignId:guid}/characters/{characterId:guid}  [Authorize]
///   200 + { Id, CampaignId, Name, Level, Sheet, OwnerUserId, ControlledByDm, HarvestConsent,
///           IsOrphan, CreatedAt } (Sheet เป็นสตริง JSON ตามที่เก็บใน jsonb)
///   404 เมื่อไม่พบ หรือเป็นตัวละครของแคมเปญอื่น
/// </summary>
public class CharacterCrudTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "character-crud-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignAId = Guid.NewGuid();
    private readonly Guid _campaignBId = Guid.NewGuid();
    private readonly Guid _campaignOwnerFixtureId = Guid.NewGuid();

    // Sprint F งานที่ 7 — แคมเปญแยกที่ owner เป็น user จริงมี JWT ใช้ได้ (ต่างจาก campaignA/B ที่ owner
    // เป็น synthetic fixture ไม่มี token) จำเป็นเพราะเทสต์ NPC ต้องมี DM ตัวจริงยิง request ผ่าน HTTP
    private readonly Guid _campaignCId = Guid.NewGuid();

    private readonly Guid _characterA1Id = Guid.NewGuid();
    private readonly Guid _characterA2Id = Guid.NewGuid();
    private readonly Guid _characterB1Id = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _userAId;
    private string _userAToken = null!;
    private Guid _userBId;
    private string _userBToken = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_userAId, _userAToken) = await RegisterAsync("a");
        (_userBId, _userBToken) = await RegisterAsync("b");
        (_dmUserId, _dmToken) = await RegisterAsync("dm");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var insertOwner = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, display_name, password_hash, created_at)
            VALUES (@id, @email, 'Character Campaign Owner', 'not-a-real-hash', now())
            """, conn))
        {
            insertOwner.Parameters.AddWithValue("id", _campaignOwnerFixtureId);
            insertOwner.Parameters.AddWithValue("email", $"character-owner-{_suffix}@example.test");
            await insertOwner.ExecuteNonQueryAsync();
        }

        foreach (var (campaignId, name) in new[]
                 {
                     (_campaignAId, $"Character Campaign A {_suffix}"),
                     (_campaignBId, $"Character Campaign B {_suffix}"),
                 })
        {
            await using var insertCampaign = new NpgsqlCommand(
                """
                INSERT INTO campaigns
                    (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
                VALUES
                    (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
                """, conn);
            insertCampaign.Parameters.AddWithValue("id", campaignId);
            insertCampaign.Parameters.AddWithValue("ownerId", _campaignOwnerFixtureId);
            insertCampaign.Parameters.AddWithValue("name", name);
            await insertCampaign.ExecuteNonQueryAsync();
        }

        await using (var insertCampaignC = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn))
        {
            insertCampaignC.Parameters.AddWithValue("id", _campaignCId);
            insertCampaignC.Parameters.AddWithValue("ownerId", _dmUserId);
            insertCampaignC.Parameters.AddWithValue("name", $"Character Campaign C (NPC) {_suffix}");
            await insertCampaignC.ExecuteNonQueryAsync();
        }

        foreach (var (campaignId, userId, role) in new[]
                 {
                     (_campaignAId, _userAId, "Player"),
                     (_campaignBId, _userBId, "Player"),
                     (_campaignCId, _dmUserId, "Dm"),
                 })
        {
            await using var insertMembership = new NpgsqlCommand(
                """
                INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
                VALUES (@id, @campaignId, @userId, @role, now())
                """, conn);
            insertMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership.Parameters.AddWithValue("campaignId", campaignId);
            insertMembership.Parameters.AddWithValue("userId", userId);
            insertMembership.Parameters.AddWithValue("role", role);
            await insertMembership.ExecuteNonQueryAsync();
        }

        // A1 = ตัวละครของ user A · A2 = quick play ที่ไม่มีเจ้าของ (owner_user_id NULL ได้ตาม schema)
        await InsertCharacterAsync(conn, _characterA1Id, _campaignAId, "Aramil", 3, _userAId, """{"hp": 24}""");
        await InsertCharacterAsync(conn, _characterA2Id, _campaignAId, "Borin", 1, null, "{}");
        await InsertCharacterAsync(conn, _characterB1Id, _campaignBId, "Outsider Hero", 5, _userBId, "{}");
    }

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, int level, Guid? ownerUserId, string sheet)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, level,
                 controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, @sheet::jsonb, @level,
                 false, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("sheet", sheet);
        cmd.Parameters.AddWithValue("level", level);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignAId, _campaignBId, _campaignCId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        var userIds = new[] { _campaignOwnerFixtureId, _userAId, _userBId, _dmUserId }
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
            Email = $"character-crud-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Character CRUD {label}",
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

    private async Task<long> CountCharactersAsync(Guid campaignId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM characters WHERE campaign_id = @campaignId", conn);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── POST /campaigns/{campaignId}/characters ───────────────────────────
    [Fact]
    public async Task Post_characters_creates_character_with_defaults_and_returns_201()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Freshly Rolled" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);
        Assert.Equal(_campaignAId, created.CampaignId);
        Assert.Equal("Freshly Rolled", created.Name);
        Assert.Equal(1, created.Level);
        Assert.Equal(_userAId, created.OwnerUserId);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            """
            SELECT campaign_id, owner_user_id, level, controlled_by_dm, harvest_consent, is_orphan, sheet::text
            FROM characters WHERE id = @id
            """, conn);
        select.Parameters.AddWithValue("id", created.Id);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ต้องมีแถว characters ที่เพิ่งสร้างจริงใน DB");
        Assert.Equal(_campaignAId, reader.GetGuid(0));
        Assert.Equal(_userAId, reader.GetGuid(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.False(reader.GetBoolean(3), "ControlledByDm ต้องเป็น false ตอนสร้าง");
        Assert.False(reader.GetBoolean(4), "HarvestConsent ต้องเป็น false ตอนสร้าง");
        Assert.False(reader.GetBoolean(5), "IsOrphan ต้องเป็น false ตอนสร้าง");
        Assert.Equal("{}", reader.GetString(6));
    }

    [Fact]
    public async Task Post_characters_honors_explicit_level()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Veteran", Level = 7 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.Equal(7, created!.Level);
    }

    /// <summary>
    /// OwnerUserId ต้องมาจากโทเคนเสมอ — ถ้ารับจาก body ได้ ใครก็สร้างตัวละคร "ของคนอื่น" ได้ทันที
    /// </summary>
    [Fact]
    public async Task Post_characters_ignores_owner_user_id_supplied_in_body()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters",
            new { Name = "Not Yours", OwnerUserId = _userBId, ControlledByDm = true, IsOrphan = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.Equal(_userAId, created!.OwnerUserId);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT owner_user_id, controlled_by_dm, is_orphan FROM characters WHERE id = @id", conn);
        select.Parameters.AddWithValue("id", created.Id);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(_userAId, reader.GetGuid(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_characters_without_usable_name_returns_400(string? name)
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_characters_with_name_longer_than_column_returns_400_not_500()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = new string('c', 101) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task Post_characters_with_level_outside_srd_range_returns_400_not_500(int level)
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Out of range", Level = level });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_characters_requires_authentication()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Anonymous" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_characters_into_a_campaign_the_user_is_not_a_member_of_is_404_and_creates_nothing()
    {
        var before = await CountCharactersAsync(_campaignBId);

        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignBId}/characters", new { Name = "Intruder" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await CountCharactersAsync(_campaignBId));
    }

    // ── GET /campaigns/{campaignId}/characters ────────────────────────────
    [Fact]
    public async Task Get_characters_lists_only_that_campaign_ordered_by_id_without_sheet()
    {
        var response = await AuthedClient(_userAToken).GetAsync($"/campaigns/{_campaignAId}/characters");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        // sheet ทั้งก้อนต้องไม่ถูกส่งมากับลิสต์ (payload บวมเปล่า + เนื้อหาละเอียดเกินจำเป็น)
        Assert.DoesNotContain("\"hp\"", raw);
        Assert.DoesNotContain("sheet", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Outsider Hero", raw);

        var list = JsonSerializer.Deserialize<CharacterListItem[]>(raw, JsonOptions);
        Assert.NotNull(list);
        var expected = new[] { _characterA1Id, _characterA2Id }
            .OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, list!.Select(c => c.Id).ToArray());

        var aramil = list.Single(c => c.Id == _characterA1Id);
        Assert.Equal("Aramil", aramil.Name);
        Assert.Equal(3, aramil.Level);
        Assert.Equal(_userAId, aramil.OwnerUserId);
        Assert.False(aramil.ControlledByDm);

        // quick play ที่ยังไม่มีเจ้าของ — ต้องคืน null ไม่ใช่พัง
        Assert.Null(list.Single(c => c.Id == _characterA2Id).OwnerUserId);
    }

    [Fact]
    public async Task Get_characters_of_a_campaign_the_user_is_not_a_member_of_is_404()
    {
        var response = await AuthedClient(_userAToken).GetAsync($"/campaigns/{_campaignBId}/characters");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Outsider Hero", raw);
    }

    [Fact]
    public async Task Other_member_cannot_read_characters_of_a_campaign_they_do_not_belong_to()
    {
        var client = AuthedClient(_userBToken);

        var listResponse = await client.GetAsync($"/campaigns/{_campaignAId}/characters");
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);

        var detailResponse = await client.GetAsync($"/campaigns/{_campaignAId}/characters/{_characterA1Id}");
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
        Assert.DoesNotContain("Aramil", await detailResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_characters_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync($"/campaigns/{_campaignAId}/characters");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET /campaigns/{campaignId}/characters/{characterId} ──────────────
    [Fact]
    public async Task Get_character_by_id_returns_full_character_including_sheet()
    {
        var response = await AuthedClient(_userAToken)
            .GetAsync($"/campaigns/{_campaignAId}/characters/{_characterA1Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var character = await response.Content.ReadFromJsonAsync<CharacterDetailResponse>(JsonOptions);
        Assert.NotNull(character);
        Assert.Equal(_characterA1Id, character!.Id);
        Assert.Equal(_campaignAId, character.CampaignId);
        Assert.Equal("Aramil", character.Name);
        Assert.Equal(3, character.Level);
        Assert.Equal(_userAId, character.OwnerUserId);
        Assert.False(character.ControlledByDm);
        Assert.False(character.HarvestConsent);
        Assert.False(character.IsOrphan);
        Assert.NotEqual(default, character.CreatedAt);

        // เทียบเนื้อใน ไม่เทียบสตริงดิบ เพราะ jsonb ปรับ whitespace ให้เป็นรูปแบบของมันเอง
        Assert.NotNull(character.Sheet);
        using var sheet = JsonDocument.Parse(character.Sheet!);
        Assert.Equal(24, sheet.RootElement.GetProperty("hp").GetInt32());
    }

    [Fact]
    public async Task Get_character_by_id_is_404_when_the_character_belongs_to_another_campaign()
    {
        var response = await AuthedClient(_userAToken)
            .GetAsync($"/campaigns/{_campaignAId}/characters/{_characterB1Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Outsider Hero", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_character_by_id_is_404_for_unknown_id()
    {
        var response = await AuthedClient(_userAToken)
            .GetAsync($"/campaigns/{_campaignAId}/characters/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_character_by_id_requires_authentication()
    {
        var response = await _factory.CreateClient()
            .GetAsync($"/campaigns/{_campaignAId}/characters/{_characterA1Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record CharacterCreatedResponse(
        Guid Id,
        Guid CampaignId,
        string Name,
        int Level,
        Guid? OwnerUserId,
        bool ControlledByDm,
        bool NpcStatsVisibleToParty,
        int CurrentHp,
        int MaxHp,
        int CurrentTempHp,
        int ArmorClass,
        string[] DamageResistances,
        string[] DamageVulnerabilities,
        string[] DamageImmunities);

    private record CharacterListItem(
        Guid Id, string Name, int Level, Guid? OwnerUserId, bool ControlledByDm,
        int? CurrentHp, int? MaxHp, int? CurrentTempHp);

    private record CharacterDetailResponse(
        Guid Id,
        Guid CampaignId,
        string Name,
        int Level,
        string? Sheet,
        Guid? OwnerUserId,
        bool ControlledByDm,
        bool NpcStatsVisibleToParty,
        bool HarvestConsent,
        bool IsOrphan,
        DateTimeOffset CreatedAt,
        int? CurrentHp,
        int? MaxHp,
        int? CurrentTempHp,
        int? ArmorClass,
        string[]? DamageResistances,
        string[]? DamageVulnerabilities,
        string[]? DamageImmunities);

    // ── Sprint B Part 2 — HP/AC/resistance ต่อยอดจาก endpoint เดิมของ Sprint A Part 3 ──────────
    // (ดู migration AddCharacterCombatStats + PROJECT-CONTEXT §4 "Sprint B Part 2" สำหรับเหตุผลเต็ม)

    [Fact]
    public async Task Post_characters_without_combat_stats_falls_back_to_safe_placeholder_defaults()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "No Stats Given" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(10, created!.MaxHp);
        Assert.Equal(10, created.CurrentHp);
        Assert.Equal(0, created.CurrentTempHp);
        Assert.Equal(10, created.ArmorClass);
        Assert.Empty(created.DamageResistances);
        Assert.Empty(created.DamageVulnerabilities);
        Assert.Empty(created.DamageImmunities);
    }

    [Fact]
    public async Task Post_characters_with_only_max_hp_specified_defaults_current_hp_to_full()
    {
        // สร้างตัวละครใหม่ควรเริ่มที่ HP เต็มเสมอถ้าไม่ได้บอก CurrentHp มาตรง ๆ
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Full Health Rookie", MaxHp = 42 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(42, created!.MaxHp);
        Assert.Equal(42, created.CurrentHp);
    }

    [Fact]
    public async Task Post_characters_with_explicit_combat_stats_persists_them_exactly()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new
            {
                Name = "Fully Statted",
                MaxHp = 30,
                CurrentHp = 18,
                CurrentTempHp = 5,
                ArmorClass = 17,
                DamageResistances = new[] { "Fire", "Cold" },
                DamageVulnerabilities = new[] { "Radiant" },
                DamageImmunities = new[] { "Poison" },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(30, created!.MaxHp);
        Assert.Equal(18, created.CurrentHp);
        Assert.Equal(5, created.CurrentTempHp);
        Assert.Equal(17, created.ArmorClass);
        Assert.Equal(new[] { "Fire", "Cold" }, created.DamageResistances);
        Assert.Equal(new[] { "Radiant" }, created.DamageVulnerabilities);
        Assert.Equal(new[] { "Poison" }, created.DamageImmunities);

        // ยืนยันแถวจริงใน DB ตรงกับ response ไม่ใช่แค่ echo กลับมาเฉย ๆ
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT current_hp, max_hp, current_temp_hp, armor_class FROM characters WHERE id = @id", conn);
        select.Parameters.AddWithValue("id", created.Id);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(18, reader.GetInt32(0));
        Assert.Equal(30, reader.GetInt32(1));
        Assert.Equal(5, reader.GetInt32(2));
        Assert.Equal(17, reader.GetInt32(3));
    }

    [Fact]
    public async Task Post_characters_with_max_hp_below_one_returns_400()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Zero HP", MaxHp = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_characters_with_current_hp_exceeding_max_hp_returns_400()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters",
            new { Name = "Overhealed", MaxHp = 10, CurrentHp = 999 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_characters_with_negative_current_hp_returns_400()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters",
            new { Name = "Negative HP", MaxHp = 10, CurrentHp = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_characters_with_armor_class_out_of_range_returns_400()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Absurd AC", ArmorClass = 999 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_characters_with_unknown_damage_type_string_returns_400_and_creates_no_row()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var countBefore = new NpgsqlCommand(
            "SELECT count(*) FROM characters WHERE campaign_id = @id", conn);
        countBefore.Parameters.AddWithValue("id", _campaignAId);
        var before = (long)(await countBefore.ExecuteScalarAsync())!;

        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters",
            new { Name = "Fake Resist", DamageResistances = new[] { "NotARealDamageType" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var countAfter = new NpgsqlCommand(
            "SELECT count(*) FROM characters WHERE campaign_id = @id", conn);
        countAfter.Parameters.AddWithValue("id", _campaignAId);
        var after = (long)(await countAfter.ExecuteScalarAsync())!;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Get_character_by_id_includes_combat_stats()
    {
        var createResponse = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters",
            new { Name = "Detail Check", MaxHp = 25, ArmorClass = 14, DamageImmunities = new[] { "Poison" } });
        var created = await createResponse.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);

        var response = await AuthedClient(_userAToken)
            .GetAsync($"/campaigns/{_campaignAId}/characters/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<CharacterDetailResponse>(JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal(25, detail!.MaxHp);
        Assert.Equal(25, detail.CurrentHp);
        Assert.Equal(14, detail.ArmorClass);
        Assert.Equal(new[] { "Poison" }, detail.DamageImmunities);
    }

    // ── Sprint F งานที่ 7: NPC/monster ─────────────────────────────────────

    [Fact]
    public async Task Dm_can_create_npc_with_controlled_by_dm_true_and_null_owner()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters",
            new { Name = "Goblin Boss", IsNpc = true, ArmorClass = 17, MaxHp = 21 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.True(created!.ControlledByDm);
        Assert.Null(created.OwnerUserId);
        Assert.True(created.NpcStatsVisibleToParty, "ไม่ส่ง StatsVisibleToParty มา = default true");
    }

    [Fact]
    public async Task Non_dm_cannot_create_npc()
    {
        var before = await CountCharactersAsync(_campaignAId);

        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/characters", new { Name = "Sneaky NPC Attempt", IsNpc = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await CountCharactersAsync(_campaignAId));
    }

    [Fact]
    public async Task Non_dm_member_sees_null_stats_for_hidden_npc_but_dm_sees_real_values()
    {
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters",
            new
            {
                Name = "Ambush Predator", IsNpc = true, StatsVisibleToParty = false, ArmorClass = 15, MaxHp = 40,
                DamageResistances = new[] { "Cold" },
                DamageVulnerabilities = new[] { "Fire" },
                DamageImmunities = new[] { "Poison" },
            });
        var npc = await createResponse.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);

        // ต้อง join แคมเปญ C ก่อนถึงจะเป็นสมาชิกเห็นได้ (RLS) — ใช้ userA เข้าเป็น player เพิ่ม
        await AddMembershipAsync(_campaignCId, _userAId, "Player");

        var playerDetail = await (await AuthedClient(_userAToken)
                .GetAsync($"/campaigns/{_campaignCId}/characters/{npc!.Id}"))
            .Content.ReadFromJsonAsync<CharacterDetailResponse>(JsonOptions);
        Assert.NotNull(playerDetail);
        Assert.Null(playerDetail!.ArmorClass);
        Assert.Null(playerDetail.CurrentHp);
        Assert.Null(playerDetail.MaxHp);
        Assert.Null(playerDetail.CurrentTempHp);
        Assert.Null(playerDetail.Sheet);
        Assert.Null(playerDetail.DamageResistances);
        Assert.Null(playerDetail.DamageVulnerabilities);
        Assert.Null(playerDetail.DamageImmunities);

        var playerList = await (await AuthedClient(_userAToken).GetAsync($"/campaigns/{_campaignCId}/characters"))
            .Content.ReadFromJsonAsync<List<CharacterListItem>>(JsonOptions);
        var listItem = playerList!.Single(c => c.Id == npc.Id);
        Assert.Null(listItem.CurrentHp);
        Assert.Null(listItem.MaxHp);
        Assert.Null(listItem.CurrentTempHp);

        var dmDetail = await (await AuthedClient(_dmToken)
                .GetAsync($"/campaigns/{_campaignCId}/characters/{npc.Id}"))
            .Content.ReadFromJsonAsync<CharacterDetailResponse>(JsonOptions);
        Assert.Equal(15, dmDetail!.ArmorClass);
        Assert.Equal(40, dmDetail.MaxHp);
        Assert.Equal(0, dmDetail.CurrentTempHp);
        Assert.Equal(new[] { "Cold" }, dmDetail.DamageResistances);
        Assert.Equal(new[] { "Fire" }, dmDetail.DamageVulnerabilities);
        Assert.Equal(new[] { "Poison" }, dmDetail.DamageImmunities);
    }

    [Fact]
    public async Task Player_character_stats_always_visible_regardless_of_npc_flag_logic()
    {
        // ตัวละครผู้เล่นปกติ (ControlledByDm=false) ต้องไม่มีวันโดนซ่อน stat แม้แต่ในแคมเปญที่มี NPC
        // ปิดการมองเห็นอยู่ด้วย — กันการเผลอ apply เงื่อนไขผิดตัว (ต้อง join ก่อนสร้างตัวละครเองได้เลย
        // เพราะ POST เช็ค membership ก่อนเสมอ)
        await AddMembershipAsync(_campaignCId, _userAId, "Player");

        var createResponse = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters", new { Name = "Real Player Hero", ArmorClass = 16 });
        var pc = await createResponse.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);

        var detail = await (await AuthedClient(_userAToken)
                .GetAsync($"/campaigns/{_campaignCId}/characters/{pc!.Id}"))
            .Content.ReadFromJsonAsync<CharacterDetailResponse>(JsonOptions);
        Assert.Equal(16, detail!.ArmorClass);
    }

    [Fact]
    public async Task Dm_can_toggle_npc_visibility()
    {
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters",
            new { Name = "Toggle Test NPC", IsNpc = true, StatsVisibleToParty = true, ArmorClass = 12 });
        var npc = await createResponse.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);

        var toggleResponse = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters/{npc!.Id}/npc-visibility", new { Visible = false });

        Assert.Equal(HttpStatusCode.OK, toggleResponse.StatusCode);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT npc_stats_visible_to_party FROM characters WHERE id = @id", conn);
        select.Parameters.AddWithValue("id", npc.Id);
        Assert.False((bool)(await select.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Non_dm_cannot_toggle_npc_visibility()
    {
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters", new { Name = "Guarded NPC", IsNpc = true });
        var npc = await createResponse.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);
        await AddMembershipAsync(_campaignCId, _userAId, "Player");

        var response = await AuthedClient(_userAToken).PutAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters/{npc!.Id}/npc-visibility", new { Visible = false });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Toggling_visibility_on_a_player_character_returns_400()
    {
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters", new { Name = "Not An NPC" });
        var pc = await createResponse.Content.ReadFromJsonAsync<CharacterCreatedResponse>(JsonOptions);

        var response = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignCId}/characters/{pc!.Id}/npc-visibility", new { Visible = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task AddMembershipAsync(Guid campaignId, Guid userId, string role)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, @role, now())
            ON CONFLICT DO NOTHING
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync();
    }
}

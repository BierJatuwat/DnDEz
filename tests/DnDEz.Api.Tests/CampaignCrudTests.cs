using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint A Part 3 — CRUD บาง ๆ ของ `campaigns` ผ่าน HTTP จริง (create + list + get เท่านั้น
/// ยังไม่มี update/delete ในเฟสนี้) ต่อ Postgres จริงที่ docker compose รันอยู่ ห้าม mock
///
/// contract ที่ implementation ต้องทำตาม (shape ต้อง match ทุกประการ ส่วน detail ออกแบบเองได้):
///
/// POST /campaigns  [Authorize]
///   body { Name (required), Mode?, RulesSystem?, DiceMode?, DeathSaveVisibility? }
///   default เมื่อไม่ส่งมา: Mode=Guided, RulesSystem=srd2024, DiceMode=SystemRoll,
///     DeathSaveVisibility=PartyVisible (ตรงกับ default ของ entity Campaign)
///   201 Created + { Id, Name, Mode }
///   ⚠️ ต้องสร้างผ่าน Postgres function `app_create_campaign()` เท่านั้น (ห้าม db.Campaigns.Add())
///     — INSERT ตรงบน campaigns ถูก RLS บล็อกอยู่แล้วหลัง migration FixCampaignListRlsPolicy
///     และ owner_user_id/DM membership ต้องมาจาก app_current_user_id() ฝั่ง DB เท่านั้น
///     (ดู migration AddCampaignCreationBootstrapFunction + CampaignBootstrapTests.cs)
///   400 เมื่อ input จากภายนอกไม่ถูกต้อง — ต้องดักที่ handler **ก่อนแตะ DB** ทุกกรณี เพราะ
///     ทั้ง request อยู่ใน transaction เดียวของ middleware: ถ้าปล่อยให้ CHECK constraint /
///     varchar length ของ Postgres เป็นคนดัก ทรานแซกชันจะ abort แล้วกลายเป็น 500
///     (บทเรียนตรงจาก code review ของ Part 2 เรื่อง DisplayName เกิน varchar(100))
///
/// GET /campaigns  [Authorize]
///   200 + array ของ { Id, Name, Mode, CreatedAt } เฉพาะแคมเปญที่ผู้ใช้เป็นสมาชิก
///   (ไม่มี campaignId ใน route → middleware ไม่ตั้ง app.campaign_id → พึ่ง policy
///    campaign_member_read = app_is_member_of(id) กรองเอง)
///   **ลำดับต้อง deterministic: CreatedAt ascending (tie-break ด้วย Id)**
///   ผู้ใช้ที่ยังไม่มีแคมเปญ → 200 + array ว่าง ไม่ใช่ 404/500
///
/// GET /campaigns/{campaignId:guid}  [Authorize]
///   200 + { Id, Name, Mode, RulesSystem, DiceMode, DeathSaveVisibility, ReactionTimeoutSeconds,
///           CharacterReviewRequired, StartingLevel, CreatedAt, OwnerUserId }
///   404 เมื่อไม่พบ/ไม่ใช่สมาชิก — ห้ามหลุดเนื้อหาแคมเปญนั้นออกไปในทุกรูปแบบ
///
/// ⚠️ ฟิลด์ที่มาจาก enum (Mode/DiceMode/DeathSaveVisibility) ต้อง serialize เป็น **ชื่อ enum
/// เป็นสตริง** ("Guided") ไม่ใช่ตัวเลข — ตรงกับที่ DB เก็บเป็น text อยู่แล้ว
/// </summary>
public class CampaignCrudTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "campaign-crud-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _userAId;
    private string _userAToken = null!;
    private Guid _userBId;
    private string _userBToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_userAId, _userAToken) = await RegisterAsync("a");
        (_userBId, _userBToken) = await RegisterAsync("b");
    }

    public async Task DisposeAsync()
    {
        var userIds = new[] { _userAId, _userBId }.Where(id => id != Guid.Empty).ToArray();

        if (userIds.Length > 0)
        {
            await using var conn = new NpgsqlConnection(OwnerConnectionString);
            await conn.OpenAsync();

            // ลบแคมเปญของ user ทั้งสองก่อน — campaign_members/sessions/characters ผูก cascade ไว้แล้ว
            await using (var deleteCampaigns = new NpgsqlCommand(
                "DELETE FROM campaigns WHERE owner_user_id = ANY(@ids)", conn))
            {
                deleteCampaigns.Parameters.AddWithValue("ids", userIds);
                await deleteCampaigns.ExecuteNonQueryAsync();
            }

            await using (var deleteUsers = new NpgsqlCommand(
                "DELETE FROM users WHERE id = ANY(@ids)", conn))
            {
                deleteUsers.Parameters.AddWithValue("ids", userIds);
                await deleteUsers.ExecuteNonQueryAsync();
            }
        }

        await _factory.DisposeAsync();
    }

    // user จริงผ่านเส้นทาง register จริง (ไม่ raw-insert) เพื่อให้ password_hash เป็นของจริง
    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"campaign-crud-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Campaign CRUD {label}",
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

    private async Task<Guid> CreateCampaignAsync(string token, string name)
    {
        var response = await AuthedClient(token).PostAsJsonAsync("/campaigns", new { Name = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CampaignCreatedResponse>(JsonOptions);
        return body!.Id;
    }

    // ── POST /campaigns ───────────────────────────────────────────────────
    [Fact]
    public async Task Post_campaigns_creates_campaign_with_entity_defaults_and_returns_201()
    {
        var client = AuthedClient(_userAToken);

        var response = await client.PostAsJsonAsync("/campaigns", new { Name = $"Defaults {_suffix}" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CampaignCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);
        Assert.Equal($"Defaults {_suffix}", created.Name);
        Assert.Equal("Guided", created.Mode);

        // อ่านกลับผ่าน endpoint จริงเพื่อยืนยัน default ที่ไม่ได้อยู่ใน response ของ POST
        var detailResponse = await client.GetAsync($"/campaigns/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<CampaignDetailResponse>(JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal("Guided", detail!.Mode);
        Assert.Equal("srd2024", detail.RulesSystem);
        Assert.Equal("SystemRoll", detail.DiceMode);
        Assert.Equal("PartyVisible", detail.DeathSaveVisibility);
        Assert.Equal(30, detail.ReactionTimeoutSeconds);
        Assert.True(detail.CharacterReviewRequired);
        Assert.Equal(1, detail.StartingLevel);
        Assert.Equal(_userAId, detail.OwnerUserId);
        Assert.NotEqual(default, detail.CreatedAt);
    }

    [Fact]
    public async Task Post_campaigns_honors_explicitly_provided_settings()
    {
        var client = AuthedClient(_userAToken);

        var response = await client.PostAsJsonAsync("/campaigns", new
        {
            Name = $"Explicit {_suffix}",
            Mode = "Custom",
            RulesSystem = "homebrew",
            DiceMode = "DmEntered",
            DeathSaveVisibility = "DmOnly",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CampaignCreatedResponse>(JsonOptions);
        Assert.Equal("Custom", created!.Mode);

        var detail = await (await client.GetAsync($"/campaigns/{created.Id}"))
            .Content.ReadFromJsonAsync<CampaignDetailResponse>(JsonOptions);
        Assert.Equal("Custom", detail!.Mode);
        Assert.Equal("homebrew", detail.RulesSystem);
        Assert.Equal("DmEntered", detail.DiceMode);
        Assert.Equal("DmOnly", detail.DeathSaveVisibility);
    }

    /// <summary>
    /// พิสูจน์ว่าเดินผ่าน app_create_campaign() จริง: owner_user_id ต้องเป็นคนที่ถือโทเคน และต้องมีแถว
    /// campaign_members role 'Dm' ของคนนั้นเกิดขึ้นพร้อมกันแบบ atomic (ไม่ใช่แค่แถว campaigns ลอย ๆ)
    /// </summary>
    [Fact]
    public async Task Post_campaigns_also_creates_dm_membership_for_the_creator()
    {
        var campaignId = await CreateCampaignAsync(_userAToken, $"Membership {_suffix}");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var select = new NpgsqlCommand(
            """
            SELECT c.owner_user_id, m.user_id, m.role
            FROM campaigns c
            JOIN campaign_members m ON m.campaign_id = c.id
            WHERE c.id = @campaignId
            """, conn);
        select.Parameters.AddWithValue("campaignId", campaignId);

        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ต้องมีทั้งแถว campaigns และ campaign_members ของผู้สร้าง");
        Assert.Equal(_userAId, reader.GetGuid(0));
        Assert.Equal(_userAId, reader.GetGuid(1));
        Assert.Equal("Dm", reader.GetString(2));
        Assert.False(await reader.ReadAsync(), "ผู้สร้างคนเดียวต้องเป็นสมาชิกเดียวตอนเพิ่งสร้าง");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_campaigns_without_usable_name_returns_400(string? name)
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync("/campaigns", new { Name = name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// input ที่ยาวเกิน varchar ของคอลัมน์ต้องถูกดักเป็น 400 ที่ handler — ไม่ใช่ปล่อยให้ Postgres
    /// โยน 22001 กลางทรานแซกชันที่คลุมทั้ง request แล้วกลายเป็น 500 (บทเรียนจาก Part 2)
    /// </summary>
    [Fact]
    public async Task Post_campaigns_with_name_longer_than_column_returns_400_not_500()
    {
        var response = await AuthedClient(_userAToken)
            .PostAsJsonAsync("/campaigns", new { Name = new string('n', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_campaigns_with_rules_system_longer_than_column_returns_400_not_500()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync("/campaigns", new
        {
            Name = $"Long rules system {_suffix}",
            RulesSystem = new string('r', 33),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// ค่า enum ที่ไม่มีอยู่จริงต้องเป็น 400 — ถ้าปล่อยผ่านไปถึง SQL จะไปตายที่ CHECK constraint
    /// (ck_campaigns_mode ฯลฯ) กลางทรานแซกชัน = 500
    /// </summary>
    [Theory]
    [InlineData("Mode", "Chaotic")]
    [InlineData("DiceMode", "PlayerEntered")]
    [InlineData("DeathSaveVisibility", "Everyone")]
    public async Task Post_campaigns_with_unknown_enum_value_returns_400_not_500(string field, string value)
    {
        var payload = new Dictionary<string, string>
        {
            ["Name"] = $"Bad enum {_suffix}",
            [field] = value,
        };

        var response = await AuthedClient(_userAToken).PostAsJsonAsync("/campaigns", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_campaigns_requires_authentication()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/campaigns", new { Name = $"Anonymous {_suffix}" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET /campaigns ────────────────────────────────────────────────────
    [Fact]
    public async Task Get_campaigns_lists_only_own_campaigns_ordered_by_creation_time()
    {
        // ตั้งชื่อให้ลำดับตามตัวอักษร (Alpha < Mid < Zeta) ต่างจากลำดับการสร้าง (Zeta, Alpha, Mid)
        // โดยเจตนา — เพื่อให้เทสต์แยกออกได้จริงว่า implementation เรียงตาม CreatedAt ไม่ใช่ Name
        var zeta = await CreateCampaignAsync(_userAToken, $"Zeta {_suffix}");
        var alpha = await CreateCampaignAsync(_userAToken, $"Alpha {_suffix}");
        var mid = await CreateCampaignAsync(_userAToken, $"Mid {_suffix}");
        await CreateCampaignAsync(_userBToken, $"Outsider campaign {_suffix}");

        var response = await AuthedClient(_userAToken).GetAsync("/campaigns");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Outsider campaign", raw);

        var list = JsonSerializer.Deserialize<CampaignListItem[]>(raw, JsonOptions);
        Assert.NotNull(list);
        Assert.Equal(new[] { zeta, alpha, mid }, list!.Select(c => c.Id).ToArray());
        Assert.Equal($"Zeta {_suffix}", list[0].Name);
        Assert.Equal("Guided", list[0].Mode);
        Assert.NotEqual(default, list[0].CreatedAt);
    }

    [Fact]
    public async Task Get_campaigns_returns_empty_array_for_user_without_campaigns()
    {
        await CreateCampaignAsync(_userAToken, $"Only A {_suffix}");

        var response = await AuthedClient(_userBToken).GetAsync("/campaigns");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<CampaignListItem[]>(JsonOptions);
        Assert.NotNull(list);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Get_campaigns_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/campaigns");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET /campaigns/{campaignId} ───────────────────────────────────────
    [Fact]
    public async Task Get_campaign_by_id_is_404_for_campaign_the_user_is_not_a_member_of()
    {
        var outsiderCampaignId = await CreateCampaignAsync(_userBToken, $"Private of B {_suffix}");

        var response = await AuthedClient(_userAToken).GetAsync($"/campaigns/{outsiderCampaignId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Private of B", raw);
    }

    [Fact]
    public async Task Get_campaign_by_id_is_404_for_unknown_id()
    {
        var response = await AuthedClient(_userAToken).GetAsync($"/campaigns/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_campaign_by_id_requires_authentication()
    {
        var campaignId = await CreateCampaignAsync(_userAToken, $"Needs auth {_suffix}");

        var response = await _factory.CreateClient().GetAsync($"/campaigns/{campaignId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record CampaignCreatedResponse(Guid Id, string Name, string Mode);

    private record CampaignListItem(Guid Id, string Name, string Mode, DateTimeOffset CreatedAt);

    private record CampaignDetailResponse(
        Guid Id,
        string Name,
        string Mode,
        string RulesSystem,
        string DiceMode,
        string DeathSaveVisibility,
        int ReactionTimeoutSeconds,
        bool CharacterReviewRequired,
        int StartingLevel,
        DateTimeOffset CreatedAt,
        Guid OwnerUserId,
        string? JoinCode);
}

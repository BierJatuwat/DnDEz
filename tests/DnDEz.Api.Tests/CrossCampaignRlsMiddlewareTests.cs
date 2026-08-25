using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint A Part 2 — พิสูจน์ middleware `SET LOCAL app.user_id`/`app.campaign_id` ผ่าน HTTP จริง
/// (auth จริงผ่าน JWT ไม่ใช่แค่ raw psql เหมือน RlsFailClosedTests.cs ของ Part 1)
///
/// สอง concern แยกกันในไฟล์นี้:
///  1) cross-campaign isolation — user A เป็นสมาชิกแค่ campaign A ต้องไม่เห็นข้อมูล campaign B เลย
///     แม้ยิง endpoint ตรง ๆ ด้วย campaignId ของ B (บทเรียนเดียวกับตอนทดสอบ RLS รอบแรกที่เจอช่องโหว่จริง
///     จากการทดสอบ ไม่ใช่จากการอ่านโค้ด — ดู docs/db-schema.md "บทเรียนตอนทดสอบ")
///  2) SET LOCAL ต้องไม่หลุดข้าม request แม้ connection ถูกดึงกลับมาใช้ซ้ำจาก pool จริง ๆ — บังคับ
///     `Maximum Pool Size=1` ผ่าน WebApplicationFactory ตัวที่สอง (override เฉพาะ connection string
///     ของ DnDEzDbContext ในเทสต์นี้ ไม่แตะ Program.cs) เพื่อไม่ต้องพึ่งดวงว่า ADO.NET pool จะ reuse
///     connection เดิมให้เอง — ถ้า implementation เผลอใช้ transaction คนละก้อนต่อ query ภายใน 1 request
///     (implicit transaction ของ EF Core) ค่าที่ SET LOCAL ไว้จะหลุดหายกลางคัน (ผูกกับ transaction
///     ไม่ใช่ connection) ต้องเปิด transaction เดียวคลุมทั้ง request ตามที่ระบุไว้ในบรีฟ
///
/// contract ที่ endpoint GET /campaigns/{campaignId:guid}/diagnostics/whoami ต้องทำตาม (shape ต้อง match
/// ทุกประการ ส่วน implementation detail อื่นออกแบบเองได้):
///   [Authorize] ผูก JWT bearer ที่ออกจาก /auth/login หรือ /auth/register (ดู AuthTests.cs)
///   200 OK + JSON { CampaignId, CampaignName, UserId } เมื่อผู้ใช้ authenticate แล้วเป็นสมาชิกแคมเปญนั้นจริง
///     (ต้องพิสูจน์ผ่าน RLS จริง — query DnDEzDbContext.Campaigns ธรรมดาไม่มี WHERE เขียนเองในโค้ด C#
///     พึ่ง SET LOCAL + policy ของ DB กรองให้ทั้งหมด)
///   404 เมื่อ campaignId ที่ขอไม่ใช่แคมเปญที่ตัวเองเป็นสมาชิก (แถวถูก RLS กรองออกจนมองไม่เห็นเลย
///     ไม่ใช่ error 403/500 — และห้ามมีเนื้อหาจริงของแคมเปญนั้นหลุดมาใน response ไม่ว่าจะทางใด)
///
/// ห้าม mock ใด ๆ ต่อ Postgres จริงที่ docker compose รันอยู่เท่านั้น
/// </summary>
public class CrossCampaignRlsMiddlewareTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    // เหมือน connection string ปกติของ dndez_app (ตรงกับ default fallback ใน Program.cs ปัจจุบัน)
    // แต่บังคับ pool size = 1 เพื่อบีบให้ request ที่ยิงติดกันได้ connection เดิมจริง ๆ จาก ADO.NET pool
    private const string AppDbConnectionStringPoolSizeOne =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app;Maximum Pool Size=1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignAId = Guid.NewGuid();
    private readonly Guid _campaignBId = Guid.NewGuid();

    // owner_user_id ของแถว campaigns เฉย ๆ (FK บังคับต้องมีจริง) ไม่ใช่ user ที่ใช้ login ในเทสต์นี้
    private readonly Guid _campaignOwnerFixtureId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private WebApplicationFactory<Program> _poolSizeOneFactory = null!;

    private Guid _userAId;
    private string _userAToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        _poolSizeOneFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<DnDEzDbContext>>();
                services.AddDbContext<DnDEzDbContext>(o =>
                    o.UseNpgsql(AppDbConnectionStringPoolSizeOne, npg => npg.UseVector())
                        .UseSnakeCaseNamingConvention());
            });
        });

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var insertOwner = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, display_name, password_hash, created_at)
            VALUES (@id, @email, 'Campaign Owner Fixture', 'not-a-real-hash', now())
            """, conn))
        {
            insertOwner.Parameters.AddWithValue("id", _campaignOwnerFixtureId);
            insertOwner.Parameters.AddWithValue("email", $"campaign-owner-{_suffix}@example.test");
            await insertOwner.ExecuteNonQueryAsync();
        }

        foreach (var (id, name) in new[] { (_campaignAId, "Campaign A"), (_campaignBId, "Campaign B") })
        {
            await using var insertCampaign = new NpgsqlCommand(
                """
                INSERT INTO campaigns
                    (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
                VALUES
                    (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
                """, conn);
            insertCampaign.Parameters.AddWithValue("id", id);
            insertCampaign.Parameters.AddWithValue("ownerId", _campaignOwnerFixtureId);
            insertCampaign.Parameters.AddWithValue("name", name);
            await insertCampaign.ExecuteNonQueryAsync();
        }

        // user A สมัครผ่าน API จริง (ไม่ raw-insert เอง) เพื่อให้ password_hash เป็นของจริงจากเส้นทาง
        // register จริง — ต้องใช้ WebApplicationFactory ปกติ (ไม่ใช่ pool-size-1) เพราะยังไม่ต้องพิสูจน์
        // เรื่อง pooling ตรงนี้
        var client = _factory.CreateClient();
        var email = $"user-a-{_suffix}@example.test";
        var registerResponse = await client.PostAsJsonAsync("/auth/register",
            new { Email = email, Password = "cross-campaign-secret-1", DisplayName = "User A" });
        registerResponse.EnsureSuccessStatusCode();
        var registerBody = await registerResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        _userAId = registerBody!.UserId;

        // user A เป็นสมาชิกแค่ campaign A เท่านั้น — ผ่าน owner role ตรง ๆ (Part 3 ยังไม่มี join API,
        // ตามที่บรีฟอนุญาตให้ seed membership แบบนี้ได้สำหรับ Part 2)
        await using (var insertMembership = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Player', now())
            """, conn))
        {
            insertMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership.Parameters.AddWithValue("campaignId", _campaignAId);
            insertMembership.Parameters.AddWithValue("userId", _userAId);
            await insertMembership.ExecuteNonQueryAsync();
        }

        var loginResponse = await client.PostAsJsonAsync("/auth/login",
            new { Email = email, Password = "cross-campaign-secret-1" });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        _userAToken = loginBody!.Token;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM campaign_members WHERE campaign_id = ANY(@ids)", conn))
        {
            cmd.Parameters.AddWithValue("ids", new[] { _campaignAId, _campaignBId });
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            cmd.Parameters.AddWithValue("ids", new[] { _campaignAId, _campaignBId });
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM users WHERE id = @ownerFixtureId OR id = @userAId", conn))
        {
            cmd.Parameters.AddWithValue("ownerFixtureId", _campaignOwnerFixtureId);
            cmd.Parameters.AddWithValue("userAId", _userAId == Guid.Empty ? Guid.NewGuid() : _userAId);
            await cmd.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
        await _poolSizeOneFactory.DisposeAsync();
    }

    private HttpClient AuthedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _userAToken);
        return client;
    }

    [Fact]
    public async Task User_sees_own_campaign_via_whoami_endpoint()
    {
        var client = AuthedClient(_factory);

        var response = await client.GetAsync($"/campaigns/{_campaignAId}/diagnostics/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhoAmIResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(_campaignAId, body!.CampaignId);
        Assert.Equal("Campaign A", body.CampaignName);
        Assert.Equal(_userAId, body.UserId);
    }

    [Fact]
    public async Task User_cannot_see_campaign_they_are_not_a_member_of_via_whoami_endpoint()
    {
        var client = AuthedClient(_factory);

        var response = await client.GetAsync($"/campaigns/{_campaignBId}/diagnostics/whoami");

        // RLS ต้องกรองแถวออกจนมองไม่เห็นเลย — ห้ามเห็นข้อมูลจริงของ campaign B ไม่ว่าในรูปแบบใด
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Campaign B", body);
    }

    [Fact]
    public async Task Set_local_session_vars_do_not_leak_across_requests_even_with_pooled_connection_forced_reuse()
    {
        // บังคับ pool size = 1 เพื่อพิสูจน์ว่าต่อให้ request ที่สองได้ connection เดิมจริง ๆ จาก pool
        // (ไม่ใช่แค่ "อาจจะ" reuse ตามธรรมชาติ) SET LOCAL ของ request แรกก็ต้องไม่หลุดมาถึง request ที่สอง
        var client = AuthedClient(_poolSizeOneFactory);

        var firstResponse = await client.GetAsync($"/campaigns/{_campaignAId}/diagnostics/whoami");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var secondResponse = await client.GetAsync($"/campaigns/{_campaignBId}/diagnostics/whoami");
        // ถ้า SET LOCAL หลุดข้าม request (ไม่ถูกล้างตอน commit/rollback ของ transaction ต่อ request)
        // request ที่สองอาจยังเห็น app.campaign_id เดิมของ request แรกค้างอยู่ หรือพฤติกรรมเพี้ยนอื่น ๆ
        Assert.Equal(HttpStatusCode.NotFound, secondResponse.StatusCode);

        // ยิงกลับไป campaign A อีกรอบ (request ที่สาม) บน connection เดิมที่ถูกใช้ซ้ำ — ต้องยังเห็นถูกต้อง
        // เหมือนเดิม พิสูจน์ว่า middleware ตั้งค่าใหม่ให้ทุกรอบจริง ไม่ใช่ "เห็นถูกแค่ครั้งแรกที่ connection สด"
        var thirdResponse = await client.GetAsync($"/campaigns/{_campaignAId}/diagnostics/whoami");
        Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);
    private record WhoAmIResponse(Guid CampaignId, string CampaignName, Guid UserId);
}

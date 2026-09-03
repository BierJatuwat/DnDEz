using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 1 — join campaign flow
///
/// contract:
///
/// PUT /campaigns/{campaignId}/join-code  [Authorize, DM เท่านั้น]
///   body { JoinCode: string? }  (4-32 ตัว, [A-Za-z0-9_-], null = ปิดไม่ให้ join)
///   200 OK + { CampaignId, JoinCode (normalize เป็นตัวพิมพ์ใหญ่) }
///   400 รูปแบบไม่ถูก · 403 ไม่ใช่ DM · 404 แคมเปญไม่มีอยู่จริง · 409 โค้ดซ้ำกับแคมเปญอื่น
///
/// POST /campaigns/join  [Authorize, rate limit 10/min ต่อ user]
///   body { JoinCode: string }
///   200 OK + { CampaignId } — เพิ่มผู้เรียกเข้า campaign_members role=Player (join ซ้ำ = no-op สำเร็จ)
///   404 โค้ดไม่พบ (ไม่แยกจากกรณีอื่นใด กัน enumeration) · 429 เกิน rate limit
///
/// GET /campaigns/{campaignId}/members  [Authorize, สมาชิกเท่านั้น]
///   200 OK + [{ UserId, DisplayName, Role, JoinedAt }] · 404 ไม่ใช่สมาชิก/แคมเปญไม่มีอยู่จริง
/// </summary>
public class CampaignMembershipEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "campaign-membership-endpoint-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _otherDmUserId;
    private string _otherDmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();

        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_otherDmUserId, _otherDmToken) = await RegisterAsync("other-dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Membership Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _otherDmUserId, $"Membership Other Campaign {_suffix}");

        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _otherDmUserId, "Dm");
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

    private async Task SetJoinCodeDirectlyAsync(Guid campaignId, string joinCode)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE campaigns SET join_code = @code WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("code", joinCode);
        cmd.Parameters.AddWithValue("id", campaignId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountMembersAsync(Guid campaignId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM campaign_members WHERE campaign_id = @campaignId", conn);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        return (long)(await cmd.ExecuteScalarAsync())!;
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

        var userIds = new[] { _dmUserId, _otherDmUserId, _playerUserId, _outsiderUserId }
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
            Email = $"membership-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Membership {label}",
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

    // ── set join code ────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_sets_join_code_successfully_and_it_is_normalized_to_uppercase()
    {
        var response = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/join-code", new { JoinCode = "owlbear-tuesday" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetJoinCodeResponse>(JsonOptions);
        Assert.Equal("OWLBEAR-TUESDAY", body!.JoinCode);
    }

    [Fact]
    public async Task Dm_clears_join_code_by_sending_null()
    {
        await SetJoinCodeDirectlyAsync(_campaignId, "TEMP-CODE");

        var response = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/join-code", new { JoinCode = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetJoinCodeResponse>(JsonOptions);
        Assert.Null(body!.JoinCode);
    }

    [Fact]
    public async Task Non_dm_cannot_set_join_code()
    {
        var response = await AuthedClient(_playerToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/join-code", new { JoinCode = "should-fail" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("ab")] // สั้นเกินไป
    [InlineData("has spaces")]
    [InlineData("has$symbol")]
    public async Task Set_join_code_with_invalid_format_returns_400(string invalidCode)
    {
        var response = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/join-code", new { JoinCode = invalidCode });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_join_code_that_collides_with_another_campaign_returns_409()
    {
        await SetJoinCodeDirectlyAsync(_otherCampaignId, "TAKEN-CODE");

        var response = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/join-code", new { JoinCode = "taken-code" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── join ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task User_who_was_never_a_member_can_join_by_code_and_then_sees_campaign_in_list()
    {
        // Definition of done จาก Sprint F งานที่ 1: user ที่ไม่เคยเกี่ยวข้องกับแคมเปญเลย
        // สมัคร → login → join ด้วย code → GET /campaigns เห็นแคมเปญนั้น
        await SetJoinCodeDirectlyAsync(_campaignId, "JOIN-ME-NOW");

        var joinResponse = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            "/campaigns/join", new { JoinCode = "join-me-now" }); // lowercase ต้อง normalize ได้

        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);
        var joinBody = await joinResponse.Content.ReadFromJsonAsync<JoinCampaignResponse>(JsonOptions);
        Assert.Equal(_campaignId, joinBody!.CampaignId);

        var listResponse = await AuthedClient(_outsiderToken).GetAsync("/campaigns");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var campaigns = await listResponse.Content.ReadFromJsonAsync<List<CampaignListItem>>(JsonOptions);
        Assert.Contains(campaigns!, c => c.Id == _campaignId);
    }

    [Fact]
    public async Task Join_with_nonexistent_code_returns_404()
    {
        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            "/campaigns/join", new { JoinCode = "no-such-code-exists" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Joining_twice_is_idempotent_and_does_not_duplicate_membership()
    {
        await SetJoinCodeDirectlyAsync(_campaignId, "IDEMPOTENT");
        var before = await CountMembersAsync(_campaignId);

        var first = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            "/campaigns/join", new { JoinCode = "IDEMPOTENT" });
        var second = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            "/campaigns/join", new { JoinCode = "IDEMPOTENT" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(before + 1, await CountMembersAsync(_campaignId));
    }

    [Fact]
    public async Task Dm_joining_own_campaigns_code_is_a_harmless_noop()
    {
        await SetJoinCodeDirectlyAsync(_campaignId, "SELF-JOIN");
        var before = await CountMembersAsync(_campaignId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            "/campaigns/join", new { JoinCode = "SELF-JOIN" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await CountMembersAsync(_campaignId));
    }

    [Fact]
    public async Task Join_beyond_rate_limit_returns_429()
    {
        var client = AuthedClient(_outsiderToken);

        for (var i = 0; i < 10; i++)
        {
            await client.PostAsJsonAsync("/campaigns/join", new { JoinCode = "rate-limit-probe" });
        }

        var eleventhResponse = await client.PostAsJsonAsync(
            "/campaigns/join", new { JoinCode = "rate-limit-probe" });

        Assert.Equal(HttpStatusCode.TooManyRequests, eleventhResponse.StatusCode);
    }

    // ── members list ─────────────────────────────────────────────────────

    [Fact]
    public async Task Member_sees_member_list_with_correct_roles()
    {
        // _playerUserId seed ไว้เป็นสมาชิกตั้งแต่ InitializeAsync แล้ว (role=Player)
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = await response.Content.ReadFromJsonAsync<List<CampaignMemberResponse>>(JsonOptions);
        Assert.NotNull(members);
        Assert.Contains(members!, m => m.UserId == _dmUserId && m.Role == "Dm");
        Assert.Contains(members!, m => m.UserId == _playerUserId && m.Role == "Player");
    }

    [Fact]
    public async Task Non_member_cannot_see_member_list()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/members");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Sprint F งานที่ 6a — join code อ่านกลับได้เฉพาะ DM ────────────────────
    // หน้า DM ต้องแสดงโค้ดปัจจุบันให้ DM เห็นหลังรีเฟรช (ไม่งั้นตั้งแล้วลืม บอกผู้เล่นไม่ได้) แต่
    // สมาชิกธรรมดาต้องไม่เห็น ไม่งั้นชวนคนนอกเข้าโต๊ะเองได้เงียบ ๆ โดย DM ไม่รู้

    [Fact]
    public async Task Dm_sees_join_code_in_campaign_detail()
    {
        await SetJoinCodeDirectlyAsync(_campaignId, "SEEN-BY-DM");

        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<CampaignDetailForJoinCode>(JsonOptions);
        Assert.Equal("SEEN-BY-DM", detail!.JoinCode);
    }

    [Fact]
    public async Task Player_member_never_sees_join_code_in_campaign_detail()
    {
        await SetJoinCodeDirectlyAsync(_campaignId, "HIDDEN-FROM-PLAYER");

        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}");

        // สมาชิกเห็นแคมเปญได้ตามปกติ (200) แต่ต้องไม่เห็นโค้ด
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<CampaignDetailForJoinCode>(JsonOptions);
        Assert.Equal(_campaignId, detail!.Id);
        Assert.Null(detail.JoinCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    /// <summary>เฉพาะฟิลด์ที่เทสต์ชุดนี้สนใจ — System.Text.Json มองข้ามฟิลด์ที่เหลือของ
    /// CampaignDetailResponse ให้เอง ไม่ต้องประกาศครบทุกตัวซ้ำกับ CampaignCrudTests</summary>
    private record CampaignDetailForJoinCode(Guid Id, string? JoinCode);

    private record SetJoinCodeResponse(Guid CampaignId, string? JoinCode);

    private record JoinCampaignResponse(Guid CampaignId);

    private record CampaignMemberResponse(Guid UserId, string DisplayName, string Role, DateTimeOffset JoinedAt);

    private record CampaignListItem(Guid Id, string Name, string Mode, DateTimeOffset CreatedAt, Guid OwnerUserId);
}

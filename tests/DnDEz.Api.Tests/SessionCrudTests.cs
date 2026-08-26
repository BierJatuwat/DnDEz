using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint A Part 3 — CRUD บาง ๆ ของ `sessions` ภายใต้แคมเปญที่เลือกแล้ว (create + list + get)
/// ต่อ Postgres จริง ห้าม mock — seed แคมเปญ/สมาชิก/เซสชันผ่าน owner role ตรง ๆ โดยเจตนา
/// เพื่อให้เทสต์ชุดนี้ไม่ผูกกับความถูกต้องของ endpoint สร้างแคมเปญ (ทดสอบแยกใน CampaignCrudTests)
///
/// contract ที่ implementation ต้องทำตาม:
///
/// POST /campaigns/{campaignId:guid}/sessions  [Authorize]
///   body { Mode (required: Online | InPerson | Companion) } · Status เริ่มที่ Draft เสมอ
///   201 Created + { Id, CampaignId, Mode, Status }
///   400 เมื่อ Mode ขาด/ไม่รู้จัก — ต้องดักที่ handler ก่อนแตะ DB (ทั้ง request อยู่ใน transaction
///     เดียวของ middleware ถ้าให้ CHECK constraint เป็นคนดักจะ abort ทั้งก้อน = 500)
///   404 เมื่อผู้เรียกไม่ใช่สมาชิกแคมเปญนั้น และ **ต้องไม่มีแถวใหม่เกิดขึ้นเลย**
///     (404 ไม่ใช่ 403 — 403 ยืนยันกลาย ๆ ว่าแคมเปญนี้มีอยู่จริง หลักเดียวกับ whoami ใน Part 2)
///
/// GET /campaigns/{campaignId:guid}/sessions  [Authorize]
///   200 + array ของ { Id, Mode, Status, JoinCode, StartedAt, EndedAt } เฉพาะของแคมเปญนั้น
///   **ลำดับต้อง deterministic: Id ascending**
///   404 เมื่อไม่ใช่สมาชิกแคมเปญนั้น (ไม่ใช่ array ว่าง — แคมเปญที่มองไม่เห็น = ไม่มีอยู่)
///
/// GET /campaigns/{campaignId:guid}/sessions/{sessionId:guid}  [Authorize]
///   200 + เซสชันนั้น · 404 เมื่อไม่พบ หรือเป็นของแคมเปญอื่น
///
/// ⚠️ Mode/Status ต้อง serialize เป็นชื่อ enum เป็นสตริง ("Online"/"Draft") ไม่ใช่ตัวเลข
/// </summary>
public class SessionCrudTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "session-crud-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignAId = Guid.NewGuid();
    private readonly Guid _campaignBId = Guid.NewGuid();
    private readonly Guid _campaignOwnerFixtureId = Guid.NewGuid();

    private readonly Guid _sessionA1Id = Guid.NewGuid();
    private readonly Guid _sessionA2Id = Guid.NewGuid();
    private readonly Guid _sessionB1Id = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _userAId;
    private string _userAToken = null!;
    private Guid _userBId;
    private string _userBToken = null!;

    /// <summary>join code ผูกกับ suffix ของรอบทดสอบ — unique index บน join_code กันชนกันเอง</summary>
    private string JoinCodeA2 => _suffix[..6].ToUpperInvariant();

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_userAId, _userAToken) = await RegisterAsync("a");
        (_userBId, _userBToken) = await RegisterAsync("b");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var insertOwner = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, display_name, password_hash, created_at)
            VALUES (@id, @email, 'Session Campaign Owner', 'not-a-real-hash', now())
            """, conn))
        {
            insertOwner.Parameters.AddWithValue("id", _campaignOwnerFixtureId);
            insertOwner.Parameters.AddWithValue("email", $"session-owner-{_suffix}@example.test");
            await insertOwner.ExecuteNonQueryAsync();
        }

        foreach (var (campaignId, name) in new[]
                 {
                     (_campaignAId, $"Session Campaign A {_suffix}"),
                     (_campaignBId, $"Session Campaign B {_suffix}"),
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

        // user A เป็นสมาชิกแค่แคมเปญ A · user B เป็นสมาชิกแค่แคมเปญ B
        foreach (var (campaignId, userId) in new[] { (_campaignAId, _userAId), (_campaignBId, _userBId) })
        {
            await using var insertMembership = new NpgsqlCommand(
                """
                INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
                VALUES (@id, @campaignId, @userId, 'Player', now())
                """, conn);
            insertMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership.Parameters.AddWithValue("campaignId", campaignId);
            insertMembership.Parameters.AddWithValue("userId", userId);
            await insertMembership.ExecuteNonQueryAsync();
        }

        await InsertSessionAsync(conn, _sessionA1Id, _campaignAId, "Online", "Draft", joinCode: null, started: false);
        await InsertSessionAsync(conn, _sessionA2Id, _campaignAId, "InPerson", "Active", JoinCodeA2, started: true);
        await InsertSessionAsync(conn, _sessionB1Id, _campaignBId, "Companion", "Draft", joinCode: null, started: false);
    }

    private static async Task InsertSessionAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string mode, string status, string? joinCode, bool started)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @campaignId, @mode, @status, @joinCode, @startedAt, NULL)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("mode", mode);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("joinCode", (object?)joinCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("startedAt", started ? DateTimeOffset.UtcNow : (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // sessions/campaign_members ผูก cascade กับ campaigns อยู่แล้ว ลบแคมเปญพอ
        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignAId, _campaignBId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        var userIds = new[] { _campaignOwnerFixtureId, _userAId, _userBId }
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
            Email = $"session-crud-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Session CRUD {label}",
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

    private async Task<long> CountSessionsAsync(Guid campaignId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM sessions WHERE campaign_id = @campaignId", conn);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── POST /campaigns/{campaignId}/sessions ─────────────────────────────
    [Fact]
    public async Task Post_sessions_creates_draft_session_and_returns_201()
    {
        var client = AuthedClient(_userAToken);

        var response = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/sessions", new { Mode = "Online" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);
        Assert.Equal(_campaignAId, created.CampaignId);
        Assert.Equal("Online", created.Mode);
        Assert.Equal("Draft", created.Status);

        // แถวต้องลงจริงในแคมเปญที่ถูกต้อง (ไม่ใช่แค่ response สวย)
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT campaign_id, mode, status FROM sessions WHERE id = @id", conn);
        select.Parameters.AddWithValue("id", created.Id);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ต้องมีแถว sessions ที่เพิ่งสร้างจริงใน DB");
        Assert.Equal(_campaignAId, reader.GetGuid(0));
        Assert.Equal("Online", reader.GetString(1));
        Assert.Equal("Draft", reader.GetString(2));
    }

    /// <summary>Status ต้องเป็น Draft เสมอ แม้ client จะพยายามยัดค่าอื่นมาใน body</summary>
    [Fact]
    public async Task Post_sessions_ignores_client_supplied_status_and_always_starts_as_draft()
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/sessions", new { Mode = "Companion", Status = "Ended" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionCreatedResponse>(JsonOptions);
        Assert.Equal("Draft", created!.Status);
        Assert.Equal("Companion", created.Mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Telepathy")]
    [InlineData("online-ish")]
    public async Task Post_sessions_with_missing_or_unknown_mode_returns_400_not_500(string? mode)
    {
        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/sessions", new { Mode = mode });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_sessions_requires_authentication()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/campaigns/{_campaignAId}/sessions", new { Mode = "Online" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_sessions_into_a_campaign_the_user_is_not_a_member_of_is_404_and_creates_nothing()
    {
        var before = await CountSessionsAsync(_campaignBId);

        var response = await AuthedClient(_userAToken).PostAsJsonAsync(
            $"/campaigns/{_campaignBId}/sessions", new { Mode = "Online" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await CountSessionsAsync(_campaignBId));
    }

    // ── GET /campaigns/{campaignId}/sessions ──────────────────────────────
    [Fact]
    public async Task Get_sessions_lists_only_sessions_of_that_campaign_ordered_by_id()
    {
        var response = await AuthedClient(_userAToken).GetAsync($"/campaigns/{_campaignAId}/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<SessionListItem[]>(JsonOptions);
        Assert.NotNull(list);

        // uuid ordering ของ Postgres = ลำดับ byte ตามรูปแบบ canonical = ordinal ของสตริง "D"
        var expected = new[] { _sessionA1Id, _sessionA2Id }
            .OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, list!.Select(s => s.Id).ToArray());
        Assert.DoesNotContain(_sessionB1Id, list.Select(s => s.Id));

        var a2 = list.Single(s => s.Id == _sessionA2Id);
        Assert.Equal("InPerson", a2.Mode);
        Assert.Equal("Active", a2.Status);
        Assert.Equal(JoinCodeA2, a2.JoinCode);
        Assert.NotNull(a2.StartedAt);
        Assert.Null(a2.EndedAt);

        var a1 = list.Single(s => s.Id == _sessionA1Id);
        Assert.Equal("Draft", a1.Status);
        Assert.Null(a1.JoinCode);
        Assert.Null(a1.StartedAt);
    }

    [Fact]
    public async Task Get_sessions_of_a_campaign_the_user_is_not_a_member_of_is_404()
    {
        var response = await AuthedClient(_userAToken).GetAsync($"/campaigns/{_campaignBId}/sessions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_sessionB1Id.ToString(), raw);
    }

    /// <summary>cross-user isolation ทิศกลับ — user B ก็ต้องไม่เห็นของแคมเปญ A เช่นกัน</summary>
    [Fact]
    public async Task Other_member_cannot_read_sessions_of_a_campaign_they_do_not_belong_to()
    {
        var client = AuthedClient(_userBToken);

        var listResponse = await client.GetAsync($"/campaigns/{_campaignAId}/sessions");
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);

        var detailResponse = await client.GetAsync($"/campaigns/{_campaignAId}/sessions/{_sessionA1Id}");
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);

        var raw = await detailResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_sessionA1Id.ToString(), raw);
    }

    [Fact]
    public async Task Get_sessions_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync($"/campaigns/{_campaignAId}/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET /campaigns/{campaignId}/sessions/{sessionId} ──────────────────
    [Fact]
    public async Task Get_session_by_id_returns_the_session()
    {
        var response = await AuthedClient(_userAToken)
            .GetAsync($"/campaigns/{_campaignAId}/sessions/{_sessionA2Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionListItem>(JsonOptions);
        Assert.NotNull(session);
        Assert.Equal(_sessionA2Id, session!.Id);
        Assert.Equal("InPerson", session.Mode);
        Assert.Equal("Active", session.Status);
        Assert.Equal(JoinCodeA2, session.JoinCode);
    }

    [Fact]
    public async Task Get_session_by_id_is_404_when_the_session_belongs_to_another_campaign()
    {
        // sessionId ของแคมเปญ B แต่ยิงผ่าน route ของแคมเปญ A ที่ตัวเองเป็นสมาชิก —
        // ต้องไม่หลุด เพราะแถวไม่ได้อยู่ในแคมเปญที่ระบุ (RLS + WHERE campaign_id ต้องกันทั้งคู่)
        var response = await AuthedClient(_userAToken)
            .GetAsync($"/campaigns/{_campaignAId}/sessions/{_sessionB1Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_session_by_id_is_404_for_unknown_id()
    {
        var response = await AuthedClient(_userAToken)
            .GetAsync($"/campaigns/{_campaignAId}/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_session_by_id_requires_authentication()
    {
        var response = await _factory.CreateClient()
            .GetAsync($"/campaigns/{_campaignAId}/sessions/{_sessionA1Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record SessionCreatedResponse(Guid Id, Guid CampaignId, string Mode, string Status);

    private record SessionListItem(
        Guid Id,
        string Mode,
        string Status,
        string? JoinCode,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt);
}

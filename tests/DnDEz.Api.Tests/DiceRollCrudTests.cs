using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint B Part 1 — endpoint แรกที่ต่อ DnDEz.Rules (rules engine, 221 เทสต์ผ่านแล้ว) เข้า API จริง
/// เป็น proof แรกว่า "AI ตัดสินใจเรียก tool, rules engine คำนวณ, DB persist" ทำงานได้จริงในโค้ด
///
/// ขอบเขตที่ตั้งใจแคบ (แบ่ง part ตามบทเรียนจาก Sprint A — ดู PROJECT-CONTEXT §4 "Sprint B"):
///  • รองรับแค่ DiceMode.SystemRoll เท่านั้น — แคมเปญที่ตั้ง DmEntered จะถูกปฏิเสธด้วย 400 ชัดเจน
///    ไม่ใช่ silent fallback ไป SystemRoll (จะขัดกับ anti-cheat ที่ล็อกไว้ใน player-side-design §5)
///  • ไม่ผูกกับ character (dice_rolls.character_id เป็น null เสมอในรอบนี้)
///  • seed **ต้องไม่โผล่ใน response เลย** (ล็อกไว้ใน PROJECT-CONTEXT §3: "ห้ามแสดง seed ให้ผู้เล่นเห็น
///    seed มีไว้ให้ DM/audit เท่านั้น") แต่ยังเก็บลง DB จริงเพื่อ replay ตรวจข้อพิพาทย้อนหลังได้
///
/// contract:
///
/// POST /campaigns/{campaignId:guid}/sessions/{sessionId:guid}/dice-rolls  [Authorize]
///   body { Expression (required), Advantage? ("None"|"Advantage"|"Disadvantage", default None) }
///   201 Created + { Id, SessionId, Expression, Rolls: [{Sides,Value,Dropped,Sign}], Total, Source }
///     **ไม่มีฟิลด์ Seed ในนี้เด็ดขาด**
///   400 เมื่อ Expression ว่าง/parse ไม่ได้ (เช่น syntax ผิด) หรือแคมเปญตั้ง DiceMode=DmEntered
///     (ยังไม่รองรับใน part นี้) — ต้องดักก่อนแตะ DB insert เสมอ (transaction เดียวคลุมทั้ง request)
///   404 เมื่อผู้เรียกไม่ใช่สมาชิกแคมเปญ หรือ session ไม่มีอยู่/เป็นของแคมเปญอื่น — ไม่มีแถวใหม่เกิดขึ้นเลย
///
/// ห้าม mock ใด ๆ ต่อ Postgres จริงที่ docker compose รันอยู่เท่านั้น (ตามธรรมเนียมทั้งโปรเจกต์)
/// </summary>
public class DiceRollCrudTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "dice-roll-crud-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignSystemRollId = Guid.NewGuid();
    private readonly Guid _campaignDmEnteredId = Guid.NewGuid();
    private readonly Guid _campaignOwnerFixtureId = Guid.NewGuid();

    private readonly Guid _sessionInSystemRollCampaignId = Guid.NewGuid();
    private readonly Guid _sessionInDmEnteredCampaignId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _memberUserId;
    private string _memberToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_memberUserId, _memberToken) = await RegisterAsync("member");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var insertOwner = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, display_name, password_hash, created_at)
            VALUES (@id, @email, 'Dice Roll Campaign Owner', 'not-a-real-hash', now())
            """, conn))
        {
            insertOwner.Parameters.AddWithValue("id", _campaignOwnerFixtureId);
            insertOwner.Parameters.AddWithValue("email", $"dice-owner-{_suffix}@example.test");
            await insertOwner.ExecuteNonQueryAsync();
        }

        foreach (var (campaignId, name, diceMode) in new[]
                 {
                     (_campaignSystemRollId, $"Dice SystemRoll Campaign {_suffix}", "SystemRoll"),
                     (_campaignDmEnteredId, $"Dice DmEntered Campaign {_suffix}", "DmEntered"),
                 })
        {
            await using var insertCampaign = new NpgsqlCommand(
                """
                INSERT INTO campaigns
                    (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
                VALUES
                    (@id, @ownerId, @name, 'Guided', 'srd2024', @diceMode, 'PartyVisible', now())
                """, conn);
            insertCampaign.Parameters.AddWithValue("id", campaignId);
            insertCampaign.Parameters.AddWithValue("ownerId", _campaignOwnerFixtureId);
            insertCampaign.Parameters.AddWithValue("name", name);
            insertCampaign.Parameters.AddWithValue("diceMode", diceMode);
            await insertCampaign.ExecuteNonQueryAsync();
        }

        // member เป็นสมาชิกแค่แคมเปญ SystemRoll (outsider ไม่เป็นสมาชิกแคมเปญไหนเลย)
        await using (var insertMembership = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Player', now())
            """, conn))
        {
            insertMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership.Parameters.AddWithValue("campaignId", _campaignSystemRollId);
            insertMembership.Parameters.AddWithValue("userId", _memberUserId);
            await insertMembership.ExecuteNonQueryAsync();
        }

        // member เป็นสมาชิกแคมเปญ DmEntered ด้วย (เพื่อทดสอบ 400 dice-mode-not-supported แยกจาก 404)
        await using (var insertMembership2 = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Player', now())
            """, conn))
        {
            insertMembership2.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership2.Parameters.AddWithValue("campaignId", _campaignDmEnteredId);
            insertMembership2.Parameters.AddWithValue("userId", _memberUserId);
            await insertMembership2.ExecuteNonQueryAsync();
        }

        await InsertSessionAsync(conn, _sessionInSystemRollCampaignId, _campaignSystemRollId);
        await InsertSessionAsync(conn, _sessionInDmEnteredCampaignId, _campaignDmEnteredId);
    }

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @campaignId, 'Online', 'Active', NULL, now(), NULL)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // dice_rolls -> sessions -> campaigns/campaign_members ผูก cascade กันหมด ลบแคมเปญพอ
        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue(
                "ids", new[] { _campaignSystemRollId, _campaignDmEnteredId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        var userIds = new[] { _campaignOwnerFixtureId, _memberUserId, _outsiderUserId }
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
            Email = $"dice-roll-crud-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Dice Roll CRUD {label}",
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

    private async Task<long> CountDiceRollsAsync(Guid sessionId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Post_dice_rolls_with_valid_expression_returns_201_with_correct_shape()
    {
        var client = AuthedClient(_memberToken);

        // ไม่มีทางได้ total คงที่จากลูกเต๋าจริง (endpoint นี้ต่อ CSPRNG จริงตามกฎที่ล็อกไว้ ไม่ mock)
        // และ "5" เดี่ยว ๆ ก็ไม่ใช่ expression ที่ถูกต้อง (DiceExpressionParser บังคับต้องมีกลุ่มลูกเต๋า
        // อย่างน้อยหนึ่งกลุ่มเสมอ ดู ReviewFollowUpTests.Parse_PureModifierWithoutDiceGroup_Throws ใน
        // DnDEz.Rules.Tests) — เทสต์นี้จึงยืนยันแค่ shape ของ response ส่วนความถูกต้องของตัวเลข
        // (range, seed, rolls breakdown) ยืนยันแยกในเทสต์ถัดไปที่ query DB จริงประกอบด้วย
        var response = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "2d6+3" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DiceRollCreatedResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);
        Assert.Equal(_sessionInSystemRollCampaignId, created.SessionId);
        Assert.Equal("2d6+3", created.Expression);
        Assert.InRange(created.Total, 5, 15); // 2d6 (2..12) + 3
        Assert.Equal(2, created.Rolls.Count);
        Assert.Equal("SystemRoll", created.Source);
    }

    [Fact]
    public async Task Post_dice_rolls_persists_row_in_db_with_seed_and_rolls_breakdown()
    {
        var client = AuthedClient(_memberToken);

        var response = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "1d20+3" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DiceRollCreatedResponse>(JsonOptions);
        Assert.NotNull(created);

        // total ต้องอยู่ในช่วงที่เป็นไปได้ของ "1d20+3" (1..20 + 3 = 4..23)
        Assert.InRange(created!.Total, 4, 23);
        var rolled = Assert.Single(created.Rolls);
        Assert.Equal(20, rolled.Sides);
        Assert.False(rolled.Dropped);
        Assert.InRange(rolled.Value, 1, 20);

        // แถวต้องลงจริงใน DB — seed ต้องมีค่าจริง (ไม่ null) ตามข้อบังคับ ck_dice_rolls_provenance
        // และ rolls jsonb เก็บ breakdown ไว้ครบ (audit trail)
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT session_id, expression, total, source, seed, rolls FROM dice_rolls WHERE id = @id", conn);
        select.Parameters.AddWithValue("id", created.Id);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ต้องมีแถว dice_rolls ที่เพิ่งสร้างจริงใน DB");
        Assert.Equal(_sessionInSystemRollCampaignId, reader.GetGuid(0));
        Assert.Equal("1d20+3", reader.GetString(1));
        Assert.Equal(created.Total, reader.GetInt32(2));
        Assert.Equal("SystemRoll", reader.GetString(3));
        Assert.False(await reader.IsDBNullAsync(4), "seed ต้องไม่เป็น null สำหรับ SystemRoll (ck_dice_rolls_provenance)");
        Assert.NotEqual(0L, reader.GetInt64(4)); // CSPRNG จริง แทบเป็นไปไม่ได้ที่จะสุ่มได้ 0 เป๊ะ
        Assert.False(await reader.IsDBNullAsync(5));
        Assert.Contains("20", reader.GetString(5)); // rolls jsonb มี sides=20 อยู่ในนั้นจริง
    }

    /// <summary>ล็อกไว้ใน PROJECT-CONTEXT §3: ห้ามแสดง seed ให้ผู้เล่นเห็นเด็ดขาด แม้เก็บลง DB จริง</summary>
    [Fact]
    public async Task Post_dice_rolls_response_never_exposes_seed_field()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "1d20" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rawJson = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("seed", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_dice_rolls_with_advantage_rolls_two_d20_and_marks_one_dropped()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "1d20", Advantage = "Advantage" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DiceRollCreatedResponse>(JsonOptions);
        Assert.NotNull(created);

        Assert.Equal(2, created!.Rolls.Count);
        Assert.Single(created.Rolls, r => r.Dropped);
        Assert.Single(created.Rolls, r => !r.Dropped);
        Assert.All(created.Rolls, r => Assert.Equal(20, r.Sides));
    }

    // ── validation (400 — ต้องไม่แตะ DB เลยตอน reject) ─────────────────

    [Fact]
    public async Task Post_dice_rolls_with_empty_expression_returns_400_and_creates_no_row()
    {
        var before = await CountDiceRollsAsync(_sessionInSystemRollCampaignId);

        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CountDiceRollsAsync(_sessionInSystemRollCampaignId));
    }

    [Fact]
    public async Task Post_dice_rolls_with_malformed_expression_returns_400_and_creates_no_row()
    {
        var before = await CountDiceRollsAsync(_sessionInSystemRollCampaignId);

        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "not-a-dice-expression" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CountDiceRollsAsync(_sessionInSystemRollCampaignId));
    }

    [Fact]
    public async Task Post_dice_rolls_with_absurdly_large_dice_count_returns_400_not_500()
    {
        // guard rail ของ DiceExpressionParser (MaxDiceCountPerGroup=100) ต้อง surface เป็น 400
        // ไม่ใช่ 500 — endpoint ต้อง catch DiceExpressionException ไม่ใช่ปล่อยให้หลุดออกไป
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "999999d20" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_dice_rolls_into_campaign_with_dm_entered_dice_mode_returns_400_not_supported_yet()
    {
        // ขอบเขต Sprint B Part 1 รองรับแค่ SystemRoll — DmEntered ต้อง reject ชัดเจน ไม่ใช่เงียบ ๆ
        // ทำเป็น SystemRoll ให้ (จะขัดกับ anti-cheat ที่ล็อกไว้ใน player-side-design §5)
        var before = await CountDiceRollsAsync(_sessionInDmEnteredCampaignId);

        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignDmEnteredId}/sessions/{_sessionInDmEnteredCampaignId}/dice-rolls",
            new { Expression = "1d20" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CountDiceRollsAsync(_sessionInDmEnteredCampaignId));
    }

    // ── authorization ───────────────────────────────────────────────────

    [Fact]
    public async Task Post_dice_rolls_without_jwt_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "1d20" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_dice_rolls_by_non_member_returns_404_and_creates_no_row()
    {
        var before = await CountDiceRollsAsync(_sessionInSystemRollCampaignId);

        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInSystemRollCampaignId}/dice-rolls",
            new { Expression = "1d20" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await CountDiceRollsAsync(_sessionInSystemRollCampaignId));
    }

    [Fact]
    public async Task Post_dice_rolls_with_session_from_another_campaign_returns_404()
    {
        // member เป็นสมาชิกทั้งสองแคมเปญ แต่ session นี้เป็นของ campaignDmEntered ไม่ใช่ campaignSystemRoll
        // ที่ระบุใน route — ต้อง 404 ไม่ใช่ยอมให้ผ่านเพราะ session id มีอยู่จริงในระบบ
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignSystemRollId}/sessions/{_sessionInDmEnteredCampaignId}/dice-rolls",
            new { Expression = "1d20" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record DieRollDetailDto(int Sides, int Value, bool Dropped, int Sign);

    private record DiceRollCreatedResponse(
        Guid Id,
        Guid SessionId,
        string Expression,
        IReadOnlyList<DieRollDetailDto> Rolls,
        int Total,
        string Source);
}

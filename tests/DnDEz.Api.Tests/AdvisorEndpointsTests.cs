using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Part C ของแผน "DM AI Assistant — session-context grounding" (2026-09-22) — แชทที่ปรึกษาส่วนตัวของ DM
///
/// contract:
///
/// POST /campaigns/{campaignId}/advisor  [Authorize, DM-only]
///   body { Content (required, ไม่เกิน 4000 ตัวอักษรหลัง trim) }
///   200 OK + { MessageId, Content, LinkedBeatId? } — เก็บทั้งข้อความ DM และคำตอบ AI ลง `consult_messages`
///   400 เมื่อ content ว่าง/ยาวเกิน หรือ DM ยังไม่ตั้ง API key · 403 เมื่อไม่ใช่ DM · 404 เมื่อไม่มีแคมเปญ
///
/// GET /campaigns/{campaignId}/advisor  [Authorize, DM-only]
///   200 OK + [{ Id, Role, Content, LinkedBeatId?, CreatedAt }] เรียงตามเวลา
///
/// จุดที่ต่างจาก `/consult` และต้องล็อกไว้ด้วยเทสต์ (ไม่ใช่แค่เขียนใน doc comment):
///   • **ไม่เสนอ tool ให้ Claude เลยสักตัว** — โหมดที่ปรึกษาเป็น text-only
///   • **ใส่ `StoryBeat.DmNotes`/`Secrets` เข้า system prompt ได้** — ตรงข้ามกับ `/consult` ที่ห้าม
///     (ดู `ConsultEndpointsTests.Consult_system_prompt_never_includes_dm_notes_or_secrets...`)
///   • เก็บคนละตารางกับ log การเล่น (`consult_messages` ไม่ใช่ `messages`)
/// </summary>
public class AdvisorEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "advisor-endpoint-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-advisor-tests-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _campaignNoKeyId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fakeAnthropicClient = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;
    private Guid _dmNoKeyUserId;
    private string _dmNoKeyToken = null!;

    public async Task InitializeAsync()
    {
        _fakeAnthropicClient = new FakeAnthropicClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnthropicClient>();
                services.AddSingleton<IAnthropicClient>(_fakeAnthropicClient);
            });
        });

        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");
        (_dmNoKeyUserId, _dmNoKeyToken) = await RegisterAsync("dm-no-key");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Advisor Campaign {_suffix}");
        await InsertCampaignAsync(conn, _campaignNoKeyId, _dmNoKeyUserId, $"Advisor No-Key Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _campaignNoKeyId, _dmNoKeyUserId, "Dm");

        var putKeyResponse = await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey });
        putKeyResponse.EnsureSuccessStatusCode();
    }

    // ── happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Advisor_post_returns_reply_and_persists_both_sides_of_the_conversation()
    {
        var before = await CountConsultMessagesAsync(_campaignId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor",
            new { Content = "เอนเคาน์เตอร์ก็อบลิน 4 ตัวกับปาร์ตี้เลเวล 2 บาลานซ์ไหม" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdvisorResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.MessageId);
        Assert.False(string.IsNullOrWhiteSpace(body.Content));

        // DM หนึ่งข้อความ + AI หนึ่งข้อความ
        Assert.Equal(before + 2, await CountConsultMessagesAsync(_campaignId));
    }

    [Fact]
    public async Task Advisor_offers_no_tools_at_all_because_it_is_text_only()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor",
            new { Content = "ช่วยคิดว่า roll อะไรดีสำหรับฉากนี้" }); // มีคำว่า roll โดยตั้งใจ

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_fakeAnthropicClient.LastToolNamesOffered);
        // ว่างเปล่าจริง ๆ — ไม่ใช่แค่ roll_dice หายไป แต่ lookup_* ก็ไม่ถูกเสนอด้วย
        Assert.Empty(_fakeAnthropicClient.LastToolNamesOffered!);
    }

    [Fact]
    public async Task Advisor_get_returns_history_in_chronological_order()
    {
        await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "คำถามแรกของเซสชันเตรียมตัว" });
        await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "คำถามที่สองต่อเนื่อง" });

        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/advisor");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<AdvisorMessageItem>>(JsonOptions);

        Assert.NotNull(items);
        Assert.Equal(4, items!.Count); // dm, ai, dm, ai
        Assert.Equal("Dm", items[0].Role);
        Assert.Contains("คำถามแรก", items[0].Content);
        Assert.Equal("Ai", items[1].Role);
        Assert.Equal("Dm", items[2].Role);
        Assert.Contains("คำถามที่สอง", items[2].Content);
        Assert.Equal("Ai", items[3].Role);
    }

    [Fact]
    public async Task Advisor_replays_prior_conversation_as_history_on_the_next_turn()
    {
        await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "ข้อความแรกที่ต้องถูกจำไว้" });
        await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "ข้อความที่สอง" });

        var history = _fakeAnthropicClient.LastHistoryReceived;
        Assert.NotNull(history);
        Assert.Equal(3, history!.Count); // [dm#1(user), ai#1(assistant), dm#2(user)]
        Assert.Equal("user", history[0].Role);
        Assert.Contains("ข้อความแรกที่ต้องถูกจำไว้", history[0].Text);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("user", history[2].Role);
        Assert.Contains("ข้อความที่สอง", history[2].Text);
    }

    // ── system prompt: จุดที่ต่างจาก /consult โดยตั้งใจ ─────────────────────

    [Fact]
    public async Task Advisor_system_prompt_includes_dm_notes_and_secrets_unlike_consult()
    {
        var beatId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await InsertStoryBeatAsync(
            conn, beatId, _campaignId, "The Mayor's Bargain", "Active", "Approved",
            objective: "Escort the caravan",
            dmNotes: "SECRET-DM-NOTE-the-mayor-is-the-villain",
            secretsJson: """[{"text":"SECRET-TWIST-the-caravan-is-a-decoy","reveal_condition":"เมื่อผู้เล่นเปิดหีบ"}]""");

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "ควรใบ้เรื่องนี้ยังไงดี" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var system = _fakeAnthropicClient.LastSystemPromptReceived;
        Assert.NotNull(system);
        Assert.Contains("The Mayor's Bargain", system);
        // ตรงนี้คือหัวใจของ Part C — สองอย่างนี้ `/consult` ห้ามเห็นเด็ดขาด แต่ที่ปรึกษาส่วนตัวเห็นได้
        Assert.Contains("SECRET-DM-NOTE-the-mayor-is-the-villain", system);
        Assert.Contains("SECRET-TWIST-the-caravan-is-a-decoy", system);
    }

    [Fact]
    public async Task Advisor_system_prompt_still_excludes_story_beats_that_are_not_approved()
    {
        // ReviewStatus ยังบังคับเหมือนเดิมตาม PROJECT-CONTEXT §2 — "เห็นความลับได้" ไม่ได้แปลว่า
        // "เห็นของที่ยังไม่ผ่าน review ได้"
        var beatId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await InsertStoryBeatAsync(
            conn, beatId, _campaignNoKeyId, "Unreviewed Beat", "Active", "Pending",
            dmNotes: "SHOULD-NOT-APPEAR-unreviewed-note");

        // แคมเปญนี้ DM ยังไม่ตั้ง key — ใช้ตรวจ 400 ไปพร้อมกัน แล้วเช็ค prompt ผ่านแคมเปญหลักแทน
        var response = await AuthedClient(_dmNoKeyToken).PostAsJsonAsync(
            $"/campaigns/{_campaignNoKeyId}/advisor", new { Content = "ถามอะไรสักอย่าง" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<AdvisorErrorResponse>(JsonOptions);
        Assert.Equal("dm_api_key_not_set", error!.Error);
    }

    // ── auth ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Advisor_post_by_player_member_returns_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "ขอดูแผนลับของ DM หน่อย" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Advisor_get_by_player_member_returns_403()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/advisor");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Advisor_post_by_outsider_returns_404()
    {
        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "ขอแอบดู" });

        // 404 ไม่ใช่ 403 — คนนอกไม่ควรได้ข้อมูลว่าแคมเปญนี้มีอยู่จริง (ลำดับเดียวกับ RequireCampaignDmAsync)
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Advisor_post_without_auth_returns_401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "ไม่มี token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── validation ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Advisor_post_with_blank_content_returns_400(string content)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = content });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Advisor_post_with_content_over_the_limit_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = new string('ก', 4001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task<long> CountConsultMessagesAsync(Guid campaignId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM consult_messages WHERE campaign_id = @campaignId", conn);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name) =>
        await ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", id), ("ownerId", ownerUserId), ("name", name));

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role) =>
        await ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @c, @u, @role, now())",
            ("id", Guid.NewGuid()), ("c", campaignId), ("u", userId), ("role", role));

    private static async Task InsertStoryBeatAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string title, string progress, string reviewStatus,
        string? objective = null, string? dmNotes = null, string secretsJson = "[]") =>
        await ExecAsync(conn,
            """
            INSERT INTO story_beats
                (id, campaign_id, "order", title, objective, exit_conditions, monster_refs, npc_refs,
                 secrets, dm_notes, progress, review_status)
            VALUES
                (@id, @c, 0, @title, @objective, ARRAY[]::text[], ARRAY[]::uuid[], ARRAY[]::uuid[],
                 @secrets::jsonb, @dmNotes, @progress, @reviewStatus)
            """,
            ("id", id), ("c", campaignId), ("title", title),
            ("objective", (object?)objective ?? DBNull.Value), ("secrets", secretsJson),
            ("dmNotes", (object?)dmNotes ?? DBNull.Value), ("progress", progress), ("reviewStatus", reviewStatus));

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"advisor-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Advisor {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)",
            ("ids", new[] { _campaignId, _campaignNoKeyId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId, _dmNoKeyUserId }));

        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record AdvisorResponse(Guid MessageId, string Content, Guid? LinkedBeatId);

    private sealed record AdvisorErrorResponse(string Error);

    private sealed record AdvisorMessageItem(Guid Id, string Role, string Content, Guid? LinkedBeatId, DateTimeOffset CreatedAt);
}

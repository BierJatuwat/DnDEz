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
/// Part D ของแผน "DM AI Assistant" (2026-09-22) — **บันทึกแคมเปญ (campaign journal)** ชั้นรายละเอียด
/// ที่อยู่ระหว่าง log ดิบกับ `recap_snapshots` ที่สรุปสั้น + tool `search_campaign_journal` ที่ให้ AI
/// ค้นเอาเองเมื่อ recap ไม่พอ
///
/// หลักการที่ผู้ใช้เลือกไว้: **"ของที่ต้องรู้ตลอด ยัดเข้า prompt / ของที่นาน ๆ ใช้ที ให้ค้นเอาเองผ่าน tool"**
/// — บันทึกจึง **ไม่** ถูกยัดเข้า `BuildSystemPromptAsync` ทั้งก้อน แต่เข้าทาง tool อย่างเดียว
///
/// contract:
///
/// POST /campaigns/{campaignId}/journal  [Authorize, DM-only]
///   body { Title (required ≤200 หลัง trim), Content (required ≤8000 หลัง trim), SessionId? }
///   200 OK + { Id, Title, Content, SessionId?, AuthoredBy, CreatedAt } — `AuthoredBy` = "Dm" เสมอ
///   สำหรับของที่สร้างผ่าน endpoint นี้ (ค่า "Ai" เป็นของที่ **ระบบ** เขียนเองตอนสรุป ไม่ใช่ค่าที่
///   ผู้เรียกกำหนดได้ — ไม่งั้น DM ปลอมที่มาของบันทึกตัวเองได้)
///   400 เมื่อ title/content ว่างหรือยาวเกิน · 400 `{error:"session_not_in_campaign"}` เมื่อ SessionId
///   ไม่ได้อยู่ในแคมเปญนี้ · 403 เมื่อไม่ใช่ DM · 404 เมื่อไม่มีแคมเปญ
///
/// GET /campaigns/{campaignId}/journal  [Authorize, DM-only]
///   200 OK + [{ Id, Title, Content, SessionId?, AuthoredBy, CreatedAt }] เรียงใหม่สุดก่อน
///
/// tool `search_campaign_journal(query)` — **2026-09-23** เสนอให้ Claude เฉพาะทางผู้ช่วย DM ช่องเดียว
///   (`/assistant`, ดู `AssistantEndpoints`) เท่านั้น
///   ⚠️ ไม่เสนอใน `/actions` (path ผู้เล่น ที่ narration ถูก broadcast เข้ากลุ่มแคมเปญจริง) ด้วยเหตุผล
///   เดียวกับที่ `OrchestratorTurnRunner.BuildSystemPromptAsync` ห้ามใส่ `StoryBeat.DmNotes`/`Secrets`:
///   บันทึกแคมเปญมีความลับของ DM ได้ ถ้า AI ค้นเจอแล้วเอ่ยถึงตอนเล่าเทิร์นของผู้เล่น เนื้อหาจะหลุดทันที
///   · RLS `dm_only` เป็นตาข่ายชั้นสอง (ดู <c>CampaignJournalRlsTests</c>) ไม่ใช่ชั้นเดียว
/// </summary>
public class CampaignJournalEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "journal-endpoint-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-journal-tests-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fakeAnthropicClient = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;
    private Guid _otherDmUserId;

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
        (_otherDmUserId, _) = await RegisterAsync("other-dm");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Journal Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _otherDmUserId, $"Journal Other Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _otherDmUserId, "Dm");
        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _otherSessionId, _otherCampaignId);

        var putKeyResponse = await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey });
        putKeyResponse.EnsureSuccessStatusCode();
    }

    // ── POST: สร้างบันทึก ─────────────────────────────────────────────────

    [Fact]
    public async Task Dm_can_create_a_journal_entry_and_it_is_marked_authored_by_dm()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal",
            new { Title = "ปาร์ตี้เผาโรงเตี๊ยม", Content = "เจ้าของโรงเตี๊ยมชื่อฮาร์เกน โกรธมาก จะตามล้างแค้น" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JournalEntryItem>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal("ปาร์ตี้เผาโรงเตี๊ยม", body.Title);
        Assert.Equal("Dm", body.AuthoredBy);
        Assert.Null(body.SessionId);
    }

    [Fact]
    public async Task Journal_entry_can_be_linked_to_a_session_of_the_same_campaign()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal",
            new { Title = "เซสชันที่ 3", Content = "ปาร์ตี้ลงไปในถ้ำ", SessionId = _sessionId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JournalEntryItem>(JsonOptions);
        Assert.Equal(_sessionId, body!.SessionId);
    }

    [Fact]
    public async Task Journal_entry_cannot_be_linked_to_a_session_of_another_campaign()
    {
        // IDOR แบบคลาสสิก — sessionId เป็นของจริง แต่ไม่ใช่ของแคมเปญนี้ ต้องไม่ผูกให้เงียบ ๆ
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal",
            new { Title = "ผูกข้ามแคมเปญ", Content = "ไม่ควรสำเร็จ", SessionId = _otherSessionId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JournalErrorResponse>(JsonOptions);
        Assert.Equal("session_not_in_campaign", error!.Error);
    }

    [Theory]
    [InlineData("", "เนื้อหาปกติ")]
    [InlineData("   ", "เนื้อหาปกติ")]
    [InlineData("หัวข้อปกติ", "")]
    [InlineData("หัวข้อปกติ", "   ")]
    public async Task Blank_title_or_content_is_rejected(string title, string content)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal", new { Title = title, Content = content });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_title_is_rejected()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal",
            new { Title = new string('ก', 201), Content = "เนื้อหาปกติ" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_content_is_rejected()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal",
            new { Title = "หัวข้อปกติ", Content = new string('ก', 8001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Player_member_cannot_create_or_read_journal_entries()
    {
        var post = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal", new { Title = "ผู้เล่นแอบเขียน", Content = "ไม่ควรสำเร็จ" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var get = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/journal");
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task Outsider_cannot_read_journal_entries()
    {
        // **แก้ความคาดหวังจาก 403 เป็น 404 ตอนทำ green (2026-09-22) พร้อมเหตุผล** — เทสต์ฉบับแรกเขียนว่า
        // ควรได้ 403 ซึ่งขัดกับพฤติกรรมจริงที่ระบบนี้ล็อกไว้แล้ว ตรวจของจริงมาแล้วสองชั้น:
        //  1. RLS ของ `campaigns` คือ `campaign_member_read | SELECT | app_is_member_of(id)` → คนนอก
        //     SELECT แถวแคมเปญไม่เห็นเลย `RequireCampaignDmAsync` จึงได้ null แล้วคืน 404 ไม่ถึงเช็ค owner
        //  2. precedent มีอยู่แล้ว: `ProposalEndpointsTests`/`AssistantEndpointsTests` ยืนยัน 404 สำหรับ
        //     คนนอกบน DM-only endpoint แบบเดียวกันนี้อยู่แล้ว
        // 404 ยัง**ปลอดภัยกว่า** 403 ด้วย เพราะไม่ยืนยันให้คนนอกรู้ว่าแคมเปญนี้มีอยู่จริง
        // (สมาชิกที่เป็นผู้เล่นยังได้ 403 ตามเดิม — เห็นแถวแคมเปญได้ แต่ไม่ใช่เจ้าของ ดูเทสต์ด้านบน)
        var get = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/journal");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Anonymous_caller_is_unauthorized()
    {
        var get = await _factory.CreateClient().GetAsync($"/campaigns/{_campaignId}/journal");
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
    }

    [Fact]
    public async Task Journal_of_a_campaign_that_does_not_exist_is_404()
    {
        var get = await AuthedClient(_dmToken).GetAsync($"/campaigns/{Guid.NewGuid()}/journal");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // ── GET: อ่านรายการ ───────────────────────────────────────────────────

    [Fact]
    public async Task Journal_list_returns_newest_first()
    {
        var client = AuthedClient(_dmToken);
        (await client.PostAsJsonAsync($"/campaigns/{_campaignId}/journal",
            new { Title = $"เก่ากว่า {_suffix}", Content = "บันทึกแรก" })).EnsureSuccessStatusCode();
        await Task.Delay(20);
        (await client.PostAsJsonAsync($"/campaigns/{_campaignId}/journal",
            new { Title = $"ใหม่กว่า {_suffix}", Content = "บันทึกที่สอง" })).EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/campaigns/{_campaignId}/journal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<JournalEntryItem>>(JsonOptions);

        Assert.NotNull(items);
        Assert.Equal($"ใหม่กว่า {_suffix}", items![0].Title);
        Assert.Equal($"เก่ากว่า {_suffix}", items[1].Title);
    }

    [Fact]
    public async Task Journal_list_never_leaks_entries_of_another_campaign()
    {
        (await AuthedClient(_dmToken).PostAsJsonAsync($"/campaigns/{_campaignId}/journal",
            new { Title = $"ของแคมเปญนี้ {_suffix}", Content = "เนื้อหา" })).EnsureSuccessStatusCode();

        // แถวของอีกแคมเปญ ใส่ตรงผ่าน owner role (ข้าม RLS) เพื่อพิสูจน์ว่า endpoint กรองจริง
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn,
            """
            INSERT INTO campaign_journal_entries (id, campaign_id, title, content, authored_by, created_at)
            VALUES (@id, @c, @title, 'ความลับของแคมเปญอื่น', 'Dm', now())
            """,
            ("id", Guid.NewGuid()), ("c", _otherCampaignId), ("title", $"ของแคมเปญอื่น {_suffix}"));

        var items = await AuthedClient(_dmToken)
            .GetFromJsonAsync<List<JournalEntryItem>>($"/campaigns/{_campaignId}/journal", JsonOptions);

        Assert.NotNull(items);
        Assert.All(items!, i => Assert.DoesNotContain("ของแคมเปญอื่น", i.Title));
    }

    // ── tool `search_campaign_journal` (ผ่านผู้ช่วย DM ช่องเดียว) ───────────────

    [Fact]
    public async Task Assistant_offers_the_journal_search_tool_to_claude()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = "คุยเฉย ๆ ไม่ต้องเรียก tool" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_fakeAnthropicClient.AgentCalls);
        Assert.Contains("search_campaign_journal", _fakeAnthropicClient.AgentCalls[0].ToolNames);
    }

    [Fact]
    public async Task Ai_can_find_a_journal_entry_through_the_search_tool()
    {
        (await AuthedClient(_dmToken).PostAsJsonAsync($"/campaigns/{_campaignId}/journal",
            new
            {
                Title = "ลอร์ดเวลรอธเปิดตัว",
                Content = "วายร้ายตัวจริงของแคมเปญคือลอร์ดเวลรอธ ปาร์ตี้ยังไม่รู้",
            })).EnsureSuccessStatusCode();

        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn(
            "", ("search_campaign_journal", new { query = "เวลรอธ" })));
        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn("เจอแล้วครับ"));

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant",
            new { Content = "ช่วยเตือนหน่อยว่าเคยบันทึกอะไรไว้เรื่องเวลรอธ" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AssistantTurnResponse>(JsonOptions);
        Assert.Equal("done", body!.Activity.Single().Status);
        Assert.Contains("ลอร์ดเวลรอธเปิดตัว", LastToolResultText(_fakeAnthropicClient.AgentCalls[1]));
    }

    [Fact]
    public async Task Journal_search_never_returns_entries_from_another_campaign()
    {
        // คำค้นเดียวกันเป๊ะ แต่แถวอยู่คนละแคมเปญ — executor ต้องกรองด้วย campaignId ของเทิร์นนี้
        var marker = $"หินลับ{_suffix}";
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn,
                """
                INSERT INTO campaign_journal_entries (id, campaign_id, title, content, authored_by, created_at)
                VALUES (@id, @c, @title, 'ความลับของแคมเปญอื่นที่ห้ามหลุด', 'Dm', now())
                """,
                ("id", Guid.NewGuid()), ("c", _otherCampaignId), ("title", marker));
        }

        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn(
            "", ("search_campaign_journal", new { query = marker })));
        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn("ไม่เจอครับ"));

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = $"มีอะไรเกี่ยวกับ {marker} บ้าง" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            "ความลับของแคมเปญอื่นที่ห้ามหลุด", LastToolResultText(_fakeAnthropicClient.AgentCalls[1]));
    }

    [Fact]
    public async Task Journal_search_with_no_match_returns_an_error_string_not_an_exception()
    {
        // convention เดียวกับ ExecuteLookupRule/ExecuteLookupMonster — tool ห้าม throw เด็ดขาด
        // ต้องคืนข้อความให้ Claude อ่านเองว่าไม่เจอ ไม่ใช่ทำให้ทั้งเทิร์นพัง
        var query = $"ไม่มีทางเจอ{_suffix}";
        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn(
            "", ("search_campaign_journal", new { query })));
        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn("หาไม่เจอครับ"));

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = $"ลองหา {query} หน่อย" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AssistantTurnResponse>(JsonOptions);
        Assert.Equal("error", body!.Activity.Single().Status);
        Assert.StartsWith("Error:", LastToolResultText(_fakeAnthropicClient.AgentCalls[1]));
    }

    [Fact]
    public async Task Journal_search_treats_sql_wildcards_as_literal_text()
    {
        // `%` ใน query ต้องไม่กลายเป็น wildcard ของ ILIKE ที่ match ทุกแถว — ไม่งั้น AI (หรือคนที่
        // ป้อนข้อความให้ AI) ดึงบันทึกทั้งแคมเปญออกมาได้ด้วยคำค้นตัวเดียว
        (await AuthedClient(_dmToken).PostAsJsonAsync($"/campaigns/{_campaignId}/journal",
            new { Title = $"บันทึกธรรมดา {_suffix}", Content = "ไม่มีเครื่องหมายเปอร์เซ็นต์อยู่เลย" }))
            .EnsureSuccessStatusCode();

        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn(
            "", ("search_campaign_journal", new { query = "%%%" })));
        _fakeAnthropicClient.AgentScript.Enqueue(new ScriptedAgentTurn("ลองแล้วครับ"));

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = "ลองค้นด้วย %%% ดู" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain($"บันทึกธรรมดา {_suffix}", LastToolResultText(_fakeAnthropicClient.AgentCalls[1]));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static string LastToolResultText(AgentCall call) =>
        string.Join("\n", call.Messages.Last(m => m.Role == "user").Content.EnumerateArray()
            .Select(b => b.GetProperty("content").GetString()));

    private sealed record AssistantActivityItem(string ToolUseId, string Tool, string Summary, string Status);

    private sealed record AssistantTurnResponse(
        string Status, Guid RunId, Guid? MessageId, string Text, List<AssistantActivityItem> Activity);

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

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId) =>
        await ExecAsync(conn,
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @campaignId, 'Online', 'Active', NULL, now(), NULL)
            """,
            ("id", id), ("campaignId", campaignId));

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
            Email = $"journal-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Journal {label}",
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
            ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId, _otherDmUserId }));

        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record JournalEntryItem(
        Guid Id, string Title, string Content, Guid? SessionId, string AuthoredBy, DateTimeOffset CreatedAt);

    private sealed record JournalErrorResponse(string Error);
}

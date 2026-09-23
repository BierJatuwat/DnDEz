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
/// 2026-09-23 — สองเส้นทางที่ AI สรุป session: recap (`SessionRecapEndpoints`) และบันทึกแคมเปญที่ AI
/// เขียน (`POST /journal/ai-summary`) + แก้/ลบบันทึก
///
/// contract หลักของ recap:
///   POST /campaigns/{c}/sessions/{s}/recap/generate [DM] → 200 { SessionId, DmVersionText, PlayerVersionText, CreatedAt }
///   PUT  /campaigns/{c}/sessions/{s}/recap          [DM] → 200 (DM เขียน/แก้เองได้ ไม่ต้องมี API key)
///   GET  /campaigns/{c}/sessions/{s}/recap          [member] → 200 / 204 · ไม่ใช่ DM ได้ DmVersionText = null
///   GET  /campaigns/{c}/recaps/latest               [member] → 200 / 204
///
/// หัวใจที่ต้องล็อกด้วยเทสต์: **ฉบับผู้เล่นสร้างจาก input ที่ไม่มีความลับตั้งแต่ต้น** — `FakeAnthropicClient`
/// ตอบกลับด้วยข้อความที่ได้รับ (echo) ดังนั้นถ้า marker ลับหลุดเข้า input ของ call ฉบับผู้เล่น มันจะโผล่
/// ใน `PlayerVersionText` ทันที เทสต์จับได้ตรง ๆ โดยไม่ต้องเดาพฤติกรรมของโมเดลจริง
/// </summary>
public class SessionSummaryEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "session-summary-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-summary-tests-0000";

    private const string PublicMarker = "PUBLIC-MARKER-party-crossed-the-bridge";
    private const string SecretMarker = "SECRET-MARKER-the-bridge-troll-is-the-mayor";
    private const string JournalMarker = "JOURNAL-MARKER-dm-note-on-the-toll";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _emptySessionId = Guid.NewGuid();
    private readonly Guid _privateOnlySessionId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();
    private readonly Guid _otherJournalEntryId = Guid.NewGuid();
    private readonly Guid _noKeyCampaignId = Guid.NewGuid();
    private readonly Guid _noKeySessionId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fake = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;
    private Guid _noKeyDmUserId;
    private string _noKeyDmToken = null!;

    public async Task InitializeAsync()
    {
        _fake = new FakeAnthropicClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnthropicClient>();
                services.AddSingleton<IAnthropicClient>(_fake);
            }));

        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");
        (_noKeyDmUserId, _noKeyDmToken) = await RegisterAsync("dm-no-key");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Summary {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        // แคมเปญอื่นของ DM คนเดียวกัน — เคส IDOR ที่สมจริงที่สุดคือ DM ส่ง id ข้ามโต๊ะของตัวเอง
        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"Summary other {_suffix}");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");
        await InsertCampaignAsync(conn, _noKeyCampaignId, _noKeyDmUserId, $"Summary no key {_suffix}");
        await InsertMembershipAsync(conn, _noKeyCampaignId, _noKeyDmUserId, "Dm");

        foreach (var (id, campaign) in new[]
                 {
                     (_sessionId, _campaignId), (_emptySessionId, _campaignId), (_privateOnlySessionId, _campaignId),
                     (_otherSessionId, _otherCampaignId), (_noKeySessionId, _noKeyCampaignId),
                 })
        {
            await ExecAsync(conn,
                """
                INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
                VALUES (@id, @c, 'Online', 'Active', NULL, now(), NULL)
                """,
                ("id", id), ("c", campaign));
        }

        await InsertMessageAsync(conn, _sessionId, "Player", false, $"We cross the bridge {PublicMarker}", 4);
        await InsertMessageAsync(conn, _sessionId, "Ai", false, "The bridge creaks under your weight.", 3);
        await InsertMessageAsync(conn, _sessionId, "Dm", true, $"How do I foreshadow? {SecretMarker}", 2);
        await InsertMessageAsync(conn, _sessionId, "Ai", true, "Drop a hint about the toll.", 1);

        await InsertMessageAsync(conn, _privateOnlySessionId, "Dm", true, $"prep only {SecretMarker}", 1);
        await InsertMessageAsync(conn, _otherSessionId, "Player", false, "other table", 1);
        await InsertMessageAsync(conn, _noKeySessionId, "Player", false, "no key table", 1);

        await ExecAsync(conn,
            """
            INSERT INTO campaign_journal_entries (id, campaign_id, session_id, title, content, authored_by, created_at)
            VALUES (@id, @c, @s, 'Toll note', @content, 'Dm', now())
            """,
            ("id", Guid.NewGuid()), ("c", _campaignId), ("s", _sessionId), ("content", JournalMarker));
        await ExecAsync(conn,
            """
            INSERT INTO campaign_journal_entries (id, campaign_id, session_id, title, content, authored_by, created_at)
            VALUES (@id, @c, NULL, 'Other table note', 'x', 'Dm', now())
            """,
            ("id", _otherJournalEntryId), ("c", _otherCampaignId));

        (await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey })).EnsureSuccessStatusCode();
    }

    // ── recap: แยกฉบับ DM/ผู้เล่นจาก input คนละชุด ───────────────────────────

    [Fact]
    public async Task Player_recap_is_built_only_from_messages_players_already_saw()
    {
        var recap = await GenerateRecapAsync(_sessionId);

        Assert.Contains(PublicMarker, recap.PlayerVersionText);
        Assert.DoesNotContain(SecretMarker, recap.PlayerVersionText);
        Assert.DoesNotContain(JournalMarker, recap.PlayerVersionText);
    }

    [Fact]
    public async Task Dm_recap_includes_private_consultations_and_session_journal_entries()
    {
        var recap = await GenerateRecapAsync(_sessionId);

        Assert.NotNull(recap.DmVersionText);
        Assert.Contains(PublicMarker, recap.DmVersionText);
        Assert.Contains(SecretMarker, recap.DmVersionText);
        Assert.Contains(JournalMarker, recap.DmVersionText);
    }

    [Fact]
    public async Task Recap_generation_offers_no_tools()
    {
        await GenerateRecapAsync(_sessionId);

        Assert.NotNull(_fake.LastToolNamesOffered);
        Assert.Empty(_fake.LastToolNamesOffered!);
    }

    [Fact]
    public async Task Player_reading_the_recap_never_receives_the_dm_version_in_the_raw_json()
    {
        await GenerateRecapAsync(_sessionId);

        foreach (var url in new[]
                 {
                     $"/campaigns/{_campaignId}/sessions/{_sessionId}/recap",
                     $"/campaigns/{_campaignId}/recaps/latest",
                 })
        {
            var response = await AuthedClient(_playerToken).GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(SecretMarker, raw);
            Assert.DoesNotContain(JournalMarker, raw);
            Assert.Contains(PublicMarker, raw);
            var body = JsonSerializer.Deserialize<RecapResponse>(raw, JsonOptions)!;
            Assert.Null(body.DmVersionText);
        }
    }

    [Fact]
    public async Task Dm_reading_the_recap_gets_both_versions()
    {
        await GenerateRecapAsync(_sessionId);

        var body = await AuthedClient(_dmToken)
            .GetFromJsonAsync<RecapResponse>($"/campaigns/{_campaignId}/sessions/{_sessionId}/recap", JsonOptions);

        Assert.NotNull(body!.DmVersionText);
        Assert.Contains(SecretMarker, body.DmVersionText);
    }

    [Fact]
    public async Task Generating_again_replaces_the_previous_recap_instead_of_piling_up()
    {
        await GenerateRecapAsync(_sessionId);
        await GenerateRecapAsync(_sessionId);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM recap_snapshots WHERE session_id = @s", conn);
        cmd.Parameters.AddWithValue("s", _sessionId);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Session_with_only_private_messages_gets_an_empty_player_recap_that_players_never_see_as_latest()
    {
        var older = await GenerateRecapAsync(_sessionId);
        var privateOnly = await GenerateRecapAsync(_privateOnlySessionId);

        Assert.Equal(string.Empty, privateOnly.PlayerVersionText);

        // ผู้เล่นต้องได้อันล่าสุดที่มีเนื้อหาจริง ไม่ใช่ recap ว่างที่เพิ่งสร้างทีหลัง
        var latest = await AuthedClient(_playerToken)
            .GetFromJsonAsync<RecapResponse>($"/campaigns/{_campaignId}/recaps/latest", JsonOptions);
        Assert.Equal(older.SessionId, latest!.SessionId);
    }

    [Fact]
    public async Task Get_returns_204_when_no_recap_exists_yet()
    {
        var response = await AuthedClient(_playerToken)
            .GetAsync($"/campaigns/{_campaignId}/sessions/{_emptySessionId}/recap");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Dm_can_write_and_edit_the_recap_by_hand_without_an_api_key()
    {
        var put = await AuthedClient(_noKeyDmToken).PutAsJsonAsync(
            $"/campaigns/{_noKeyCampaignId}/sessions/{_noKeySessionId}/recap",
            new { DmVersionText = "dm hand-written", PlayerVersionText = "player hand-written" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var edit = await AuthedClient(_noKeyDmToken).PutAsJsonAsync(
            $"/campaigns/{_noKeyCampaignId}/sessions/{_noKeySessionId}/recap",
            new { DmVersionText = "dm edited", PlayerVersionText = "player edited" });
        var body = await edit.Content.ReadFromJsonAsync<RecapResponse>(JsonOptions);

        Assert.Equal("dm edited", body!.DmVersionText);
        Assert.Equal("player edited", body.PlayerVersionText);
    }

    [Fact]
    public async Task Player_cannot_generate_or_edit_a_recap()
    {
        var generate = await AuthedClient(_playerToken).PostAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/recap/generate", null);
        var put = await AuthedClient(_playerToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/recap",
            new { DmVersionText = "x", PlayerVersionText = "y" });

        Assert.Equal(HttpStatusCode.Forbidden, generate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Outsider_gets_404_reading_a_recap()
    {
        await GenerateRecapAsync(_sessionId);

        var response = await AuthedClient(_outsiderToken)
            .GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/recap");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Session_from_another_campaign_is_rejected_even_for_the_same_dm()
    {
        var generate = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/sessions/{_otherSessionId}/recap/generate", null);
        var get = await AuthedClient(_dmToken)
            .GetAsync($"/campaigns/{_campaignId}/sessions/{_otherSessionId}/recap");

        Assert.Equal(HttpStatusCode.NotFound, generate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Generate_without_api_key_or_messages_returns_400_with_reason()
    {
        var noKey = await AuthedClient(_noKeyDmToken).PostAsync(
            $"/campaigns/{_noKeyCampaignId}/sessions/{_noKeySessionId}/recap/generate", null);
        var empty = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/sessions/{_emptySessionId}/recap/generate", null);

        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("dm_api_key_not_set", (await noKey.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions))!.Error);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("session_has_no_messages", (await empty.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions))!.Error);
    }

    [Fact]
    public async Task Advisor_picks_up_the_generated_dm_recap_as_memory()
    {
        // ปิดวง: `/advisor` อ่าน recap ล่าสุดเข้า system prompt มาตั้งแต่ Part C แต่ไม่เคยมีอะไรเขียน
        await GenerateRecapAsync(_sessionId);

        var advisor = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/advisor", new { Content = "ตอนที่แล้วเกิดอะไรขึ้นบ้าง" });
        Assert.Equal(HttpStatusCode.OK, advisor.StatusCode);

        Assert.Contains(SecretMarker, _fake.LastSystemPromptReceived);
    }

    // ── recap: ชั้น RLS ──────────────────────────────────────────────────────

    [Fact]
    public async Task Player_can_select_recap_rows_but_not_write_them_at_the_db_layer()
    {
        await GenerateRecapAsync(_sessionId);

        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await using (var select = new NpgsqlCommand(
                         "SELECT count(*) FROM recap_snapshots WHERE session_id = @s", conn, tx))
        {
            select.Parameters.AddWithValue("s", _sessionId);
            Assert.Equal(1L, (long)(await select.ExecuteScalarAsync())!);
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO recap_snapshots (id, session_id, dm_version_text, player_version_text, created_at)
            VALUES (@id, @s, 'forged', 'forged', now())
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("s", _sessionId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Player_update_of_a_recap_row_affects_nothing_at_the_db_layer()
    {
        await GenerateRecapAsync(_sessionId);

        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        // UPDATE ที่ policy USING ไม่ผ่านจะเห็น 0 แถว (ไม่ throw) — ตรวจจำนวนแถวที่โดนแก้แทน
        await using var update = new NpgsqlCommand(
            "UPDATE recap_snapshots SET player_version_text = 'forged' WHERE session_id = @s", conn, tx);
        update.Parameters.AddWithValue("s", _sessionId);
        Assert.Equal(0, await update.ExecuteNonQueryAsync());
    }

    // ── บันทึกแคมเปญที่ AI สรุป + แก้/ลบ ───────────────────────────────────────

    [Fact]
    public async Task Ai_summary_writes_an_ai_authored_journal_entry_from_the_full_log()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/ai-summary", new { SessionId = _sessionId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await response.Content.ReadFromJsonAsync<JournalEntryItem>(JsonOptions);
        Assert.Equal("Ai", entry!.AuthoredBy);
        Assert.Equal(_sessionId, entry.SessionId);
        Assert.StartsWith("สรุปเซสชัน ", entry.Title);
        // บันทึกเป็น DM-only จึงใช้ log เต็มรวมที่ปรึกษาลับได้
        Assert.Contains(SecretMarker, entry.Content);
        Assert.Contains(PublicMarker, entry.Content);
        Assert.Empty(_fake.LastToolNamesOffered!);
    }

    [Fact]
    public async Task Ai_summary_uses_the_custom_title_when_given()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/ai-summary", new { SessionId = _sessionId, Title = "ข้ามสะพาน" });

        var entry = await response.Content.ReadFromJsonAsync<JournalEntryItem>(JsonOptions);
        Assert.Equal("ข้ามสะพาน", entry!.Title);
    }

    [Fact]
    public async Task Ai_summary_rejects_a_session_from_another_campaign()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/ai-summary", new { SessionId = _otherSessionId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("session_not_in_campaign",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions))!.Error);
    }

    [Fact]
    public async Task Ai_summary_of_an_empty_session_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/ai-summary", new { SessionId = _emptySessionId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("session_has_no_messages",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions))!.Error);
    }

    [Fact]
    public async Task Player_cannot_request_an_ai_summary_or_edit_or_delete_entries()
    {
        var entryId = await CreateDmEntryAsync();

        var summary = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/ai-summary", new { SessionId = _sessionId });
        var put = await AuthedClient(_playerToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/{entryId}", new { Title = "x", Content = "y" });
        var delete = await AuthedClient(_playerToken).DeleteAsync($"/campaigns/{_campaignId}/journal/{entryId}");

        Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Dm_can_edit_an_ai_entry_and_it_keeps_its_ai_origin()
    {
        var created = await (await AuthedClient(_dmToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/journal/ai-summary", new { SessionId = _sessionId }))
            .Content.ReadFromJsonAsync<JournalEntryItem>(JsonOptions);

        var put = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/{created!.Id}", new { Title = "แก้แล้ว", Content = "เนื้อหาที่ DM แก้" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var edited = await put.Content.ReadFromJsonAsync<JournalEntryItem>(JsonOptions);
        Assert.Equal("แก้แล้ว", edited!.Title);
        Assert.Equal("เนื้อหาที่ DM แก้", edited.Content);
        Assert.Equal("Ai", edited.AuthoredBy);
    }

    [Fact]
    public async Task Edit_and_delete_of_an_entry_from_another_campaign_return_404()
    {
        var put = await AuthedClient(_dmToken).PutAsJsonAsync(
            $"/campaigns/{_campaignId}/journal/{_otherJournalEntryId}", new { Title = "x", Content = "y" });
        var delete = await AuthedClient(_dmToken)
            .DeleteAsync($"/campaigns/{_campaignId}/journal/{_otherJournalEntryId}");

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // และแถวของแคมเปญอื่นต้องยังอยู่จริง ไม่ใช่แค่ตอบ 404 แต่แอบลบไปแล้ว
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT title FROM campaign_journal_entries WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _otherJournalEntryId);
        Assert.Equal("Other table note", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Dm_can_delete_an_entry_once()
    {
        var entryId = await CreateDmEntryAsync();

        var first = await AuthedClient(_dmToken).DeleteAsync($"/campaigns/{_campaignId}/journal/{entryId}");
        var second = await AuthedClient(_dmToken).DeleteAsync($"/campaigns/{_campaignId}/journal/{entryId}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<RecapResponse> GenerateRecapAsync(Guid sessionId)
    {
        var response = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/sessions/{sessionId}/recap/generate", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RecapResponse>(JsonOptions))!;
    }

    private async Task<Guid> CreateDmEntryAsync()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/journal", new { Title = "DM note", Content = "hand written" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JournalEntryItem>(JsonOptions))!.Id;
    }

    private static Task InsertMessageAsync(
        NpgsqlConnection conn, Guid sessionId, string authorType, bool dmPrivate, string content, int minutesAgo) =>
        ExecAsync(conn,
            """
            INSERT INTO messages (id, session_id, author_type, content, source, dm_private, created_at)
            VALUES (@id, @s, @author, @content, 'Player', @private, now() - make_interval(mins => @ago))
            """,
            ("id", Guid.NewGuid()), ("s", sessionId), ("author", authorType), ("content", content),
            ("private", dmPrivate), ("ago", minutesAgo));

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAsAsync(Guid userId)
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
                         "SELECT set_config('app.user_id', @u, true), set_config('app.campaign_id', @c, true)", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId.ToString());
            cmd.Parameters.AddWithValue("c", _campaignId.ToString());
            await cmd.ExecuteScalarAsync();
        }

        return (conn, tx);
    }

    private static Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid owner, string name) =>
        ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @owner, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", id), ("owner", owner), ("name", name));

    private static Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role) =>
        ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @c, @u, @role, now())",
            ("id", Guid.NewGuid()), ("c", campaignId), ("u", userId), ("role", role));

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
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"summary-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Summary {label}",
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
            ("ids", new[] { _campaignId, _otherCampaignId, _noKeyCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId, _noKeyDmUserId }));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record ErrorResponse(string Error);

    private sealed record RecapResponse(Guid SessionId, string? DmVersionText, string PlayerVersionText, DateTimeOffset CreatedAt);

    private sealed record JournalEntryItem(
        Guid Id, Guid? SessionId, string Title, string Content, string AuthoredBy, DateTimeOffset CreatedAt);
}

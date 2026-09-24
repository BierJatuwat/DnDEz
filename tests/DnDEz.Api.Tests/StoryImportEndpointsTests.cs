using System.Net;
using System.Net.Http.Headers;
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
/// 2026-09-24 — นำเข้าเนื้อเรื่องจากเอกสาร (`StoryImportEndpoints`) + ด่านตรวจของ DM (`POST .../beats/{id}/approve`)
/// + บั๊กเดิมที่เจอระหว่างทำ: beat ที่ DM พิมพ์เองได้ `ReviewStatus.Pending` ทำให้ AI มองไม่เห็นตลอดไป
///
/// หัวใจของไฟล์นี้: **beat จากเอกสารต้องไม่ถึง prompt ของ AI จนกว่า DM จะ approve** (PROJECT-CONTEXT §2 "ห้าม commit
/// อัตโนมัติ") — ตรวจที่ system prompt ที่ fake ได้รับจริง ไม่ใช่แค่ค่าในคอลัมน์
///
/// Anthropic เป็น fake (`FakeAnthropicClient.ExtractScript`) · MinIO/Postgres เป็นของจริงตาม convention โปรเจกต์
/// </summary>
public class StoryImportEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "story-import-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-story-import-0000";

    private static readonly byte[] PdfBytes = "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF"u8.ToArray();
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _existingBeatId = Guid.NewGuid();
    private readonly Guid _otherCampaignPendingBeatId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fake = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;
    private Guid _keylessDmUserId;
    private string _keylessDmToken = null!;
    private readonly Guid _keylessCampaignId = Guid.NewGuid();

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
        (_keylessDmUserId, _keylessDmToken) = await RegisterAsync("keyless");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await InsertCampaignAsync(conn, _campaignId, _dmUserId);
        await InsertCampaignAsync(conn, _otherCampaignId, _outsiderUserId);
        await InsertCampaignAsync(conn, _keylessCampaignId, _keylessDmUserId);
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _outsiderUserId, "Dm");
        await InsertMembershipAsync(conn, _keylessCampaignId, _keylessDmUserId, "Dm");
        await ExecAsync(conn,
            "INSERT INTO sessions (id, campaign_id, mode, status, started_at) VALUES (@id, @c, 'Online', 'Active', now())",
            ("id", _sessionId), ("c", _campaignId));
        await InsertBeatAsync(conn, _existingBeatId, _campaignId, 0, "ท่าเรือ", "Approved");
        await InsertBeatAsync(conn, _otherCampaignPendingBeatId, _otherCampaignId, 0, "ของแคมเปญอื่น", "Pending");

        (await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey })).EnsureSuccessStatusCode();
    }

    // ── บั๊กเดิม: beat ที่ DM พิมพ์เอง ─────────────────────────────────────────

    [Fact]
    public async Task Beat_typed_by_the_dm_is_approved_immediately()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/beats", new { Title = "ถ้ำก็อบลิน" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var beat = await response.Content.ReadFromJsonAsync<BeatItem>(JsonOptions);
        Assert.Equal("Approved", beat!.ReviewStatus);
    }

    [Fact]
    public async Task Beat_typed_by_the_dm_reaches_the_assistant_once_it_is_active()
    {
        var created = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/beats",
            new { Title = $"ปราสาทเครกมอว์ {_suffix}", DmNotes = "บักเบียร์ขังเจ้าเมืองไว้ชั้นใต้ดิน" });
        var beat = await created.Content.ReadFromJsonAsync<BeatItem>(JsonOptions);
        (await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = beat!.Id }))
            .EnsureSuccessStatusCode();

        await AskAssistantAsync();

        Assert.Contains($"Current story beat: ปราสาทเครกมอว์ {_suffix}", _fake.LastSystemPromptReceived);
        Assert.Contains("บักเบียร์ขังเจ้าเมืองไว้ชั้นใต้ดิน", _fake.LastSystemPromptReceived);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"text\":\"object not array\"}")]
    [InlineData("[{\"reveal_condition\":\"no text\"}]")]
    [InlineData("[{\"text\":\"   \"}]")]
    [InlineData("[\"plain string\"]")]
    public async Task Malformed_secrets_are_rejected_on_create_and_update(string secrets)
    {
        var create = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/beats", new { Title = "ทดสอบ", Secrets = secrets });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        var update = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/beats/{_existingBeatId}", new { Secrets = secrets });
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Well_formed_secrets_are_accepted()
    {
        const string secrets = "[{\"text\":\"เจ้าเมืองคือหมาป่า\",\"reveal_condition\":\"คืนพระจันทร์เต็มดวง\"},{\"text\":\"มีทางลับ\"}]";

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/beats", new { Title = "ทดสอบความลับ", Secrets = secrets });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── อัปโหลด + แยก beat ─────────────────────────────────────────────────

    [Fact]
    public async Task Uploaded_pdf_becomes_pending_beats_appended_after_existing_ones()
    {
        _fake.ExtractScript.Enqueue(new
        {
            beats = new object[]
            {
                new { title = "ซุ่มโจมตีบนถนน", summary = "ก็อบลินโผล่จากพุ่มไม้", dm_notes = "ก็อบลิน 4 ตัว" },
                new { title = "หมู่บ้านฟานดาลิน", summary = "หมู่บ้านชายแดน" },
            },
        });

        var response = await UploadAsync(_dmToken, _campaignId, ("adventure.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ImportResponse>(JsonOptions);
        Assert.Equal(["ซุ่มโจมตีบนถนน", "หมู่บ้านฟานดาลิน"], body!.Beats.Select(b => b.Title));
        Assert.All(body.Beats, b => Assert.Equal("Pending", b.ReviewStatus));
        Assert.Equal([1, 2], body.Beats.Select(b => b.Order));
        Assert.Equal("adventure.pdf", body.Documents.Single().FileName);

        await using var conn = await OpenAsync();
        Assert.Equal(1L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM source_documents WHERE campaign_id = @c AND ocr_status = 'Completed'", ("c", _campaignId)));
    }

    [Fact]
    public async Task Imported_beats_stay_out_of_the_ai_prompt_until_the_dm_approves_them()
    {
        var title = $"วิหารใต้น้ำ {_suffix}";
        _fake.ExtractScript.Enqueue(new { beats = new object[] { new { title, summary = "วิหารจมน้ำ" } } });
        var imported = await (await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes)))
            .Content.ReadFromJsonAsync<ImportResponse>(JsonOptions);

        await AskAssistantAsync();
        Assert.DoesNotContain(title, _fake.LastSystemPromptReceived);

        var approve = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/beats/{imported!.Beats.Single().Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("Approved", (await approve.Content.ReadFromJsonAsync<BeatItem>(JsonOptions))!.ReviewStatus);

        await AskAssistantAsync();
        Assert.Contains(title, _fake.LastSystemPromptReceived);
    }

    [Fact]
    public async Task Media_type_sent_to_claude_comes_from_file_bytes_not_the_client_header()
    {
        _fake.ExtractScript.Enqueue(new { beats = new object[] { new { title = "หน้าเดียว", summary = "x" } } });

        await UploadAsync(_dmToken, _campaignId,
            ("photo.png", "image/png", PdfBytes),
            ("page.pdf", "application/pdf", PngBytes));

        var documents = _fake.ExtractCalls.Single().Documents;
        Assert.Equal(["application/pdf", "image/png"], documents.Select(d => d.MediaType));
    }

    [Fact]
    public async Task File_that_is_not_a_pdf_or_image_is_rejected_before_calling_claude()
    {
        var executable = "MZ\u0090\0 this is not a pdf"u8.ToArray();

        var response = await UploadAsync(_dmToken, _campaignId, ("adventure.pdf", "application/pdf", executable));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_file_type", (await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions))!.Error);
        Assert.Empty(_fake.ExtractCalls);
    }

    [Fact]
    public async Task Oversized_image_is_rejected_before_calling_claude()
    {
        var bigJpeg = new byte[4 * 1024 * 1024];
        bigJpeg[0] = 0xFF;
        bigJpeg[1] = 0xD8;
        bigJpeg[2] = 0xFF;

        var response = await UploadAsync(_dmToken, _campaignId, ("page.jpg", "image/jpeg", bigJpeg));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("image_too_large", (await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions))!.Error);
        Assert.Empty(_fake.ExtractCalls);
    }

    [Fact]
    public async Task More_than_ten_files_is_rejected()
    {
        var files = Enumerable.Range(0, 11).Select(i => ($"p{i}.png", "image/png", PngBytes)).ToArray();

        var response = await UploadAsync(_dmToken, _campaignId, files);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_fake.ExtractCalls);
    }

    [Fact]
    public async Task Player_cannot_import_and_outsider_gets_404()
    {
        var asPlayer = await UploadAsync(_playerToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));
        var asOutsider = await UploadAsync(_outsiderToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.Forbidden, asPlayer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asOutsider.StatusCode);
        Assert.Empty(_fake.ExtractCalls);
    }

    [Fact]
    public async Task Dm_without_an_api_key_is_told_so()
    {
        var response = await UploadAsync(_keylessDmToken, _keylessCampaignId, ("a.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("dm_api_key_not_set", (await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions))!.Error);
    }

    [Fact]
    public async Task Failed_extraction_saves_nothing()
    {
        _fake.ExtractScript.Enqueue(FakeAnthropicClient.ExtractFailure);

        var response = await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await AssertNothingSavedAsync();
    }

    [Fact]
    public async Task Document_with_no_beats_saves_nothing()
    {
        _fake.ExtractScript.Enqueue(new { beats = Array.Empty<object>() });

        var response = await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertNothingSavedAsync();
    }

    [Fact]
    public async Task Malformed_extraction_output_is_rejected_not_saved()
    {
        _fake.ExtractScript.Enqueue(new { scenes = "not the schema" });

        var response = await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await AssertNothingSavedAsync();
    }

    [Fact]
    public async Task Extraction_output_is_cleaned_before_it_is_saved()
    {
        _fake.ExtractScript.Enqueue(new
        {
            beats = new object[]
            {
                new { summary = "ไม่มีชื่อ ต้องถูกข้าม" },
                new
                {
                    title = new string('ก', 500),
                    summary = "  มีช่องว่างหัวท้าย  ",
                    exit_conditions = new object[] { "หนีได้", "", 42 },
                    secrets = new object[]
                    {
                        new { text = "เจ้าเมืองคือหมาป่า", reveal_condition = "คืนเดือนเต็ม" },
                        new { reveal_condition = "ไม่มี text ต้องถูกข้าม" },
                        new { text = "มีทางลับ" },
                    },
                },
            },
        });

        var response = await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        var beat = (await response.Content.ReadFromJsonAsync<ImportResponse>(JsonOptions))!.Beats.Single();
        Assert.Equal(200, beat.Title.Length);
        Assert.Equal("มีช่องว่างหัวท้าย", beat.Summary);
        Assert.Equal(["หนีได้"], beat.ExitConditions);
        using var secrets = JsonDocument.Parse(beat.Secrets);
        Assert.Equal(2, secrets.RootElement.GetArrayLength());
        Assert.Equal("คืนเดือนเต็ม", secrets.RootElement[0].GetProperty("reveal_condition").GetString());
        Assert.False(secrets.RootElement[1].TryGetProperty("reveal_condition", out _));
    }

    [Fact]
    public async Task Upload_counts_against_the_dm_storage_quota()
    {
        _fake.ExtractScript.Enqueue(new { beats = new object[] { new { title = "หนึ่ง", summary = "x" } } });

        await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        await using var conn = await OpenAsync();
        Assert.Equal((long)PdfBytes.Length, await ScalarAsync<long>(conn,
            "SELECT used_bytes FROM storage_quotas WHERE user_id = @u", ("u", _dmUserId)));
    }

    [Fact]
    public async Task Upload_over_quota_is_rejected_before_calling_claude()
    {
        await using (var conn = await OpenAsync())
        {
            await ExecAsync(conn,
                """
                INSERT INTO storage_quotas (user_id, used_bytes, limit_bytes) VALUES (@u, 0, 10)
                ON CONFLICT (user_id) DO UPDATE SET used_bytes = 0, limit_bytes = 10
                """, ("u", _dmUserId));
        }

        var response = await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("quota_exceeded", (await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions))!.Error);
        Assert.Empty(_fake.ExtractCalls);
    }

    [Fact]
    public async Task Quota_used_up_by_another_upload_during_extraction_is_respected_not_overwritten()
    {
        // ช่วงที่ Claude อ่านเอกสารยาวได้หลายนาที — อัปโหลดอีกคำขอ commit แทรกได้ · ถ้าเขียน UsedBytes จากค่าที่
        // อ่านไว้ก่อนเรียก Claude จะทับการใช้งานของอีกคำขอ (lost update) และทะลุ quota
        await using (var conn = await OpenAsync())
        {
            await ExecAsync(conn,
                """
                INSERT INTO storage_quotas (user_id, used_bytes, limit_bytes) VALUES (@u, 0, 1000)
                ON CONFLICT (user_id) DO UPDATE SET used_bytes = 0, limit_bytes = 1000
                """, ("u", _dmUserId));
        }

        _fake.ExtractScript.Enqueue(new Func<object>(() =>
        {
            using var conn = new NpgsqlConnection(OwnerConnectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("UPDATE storage_quotas SET used_bytes = 990 WHERE user_id = @u", conn);
            cmd.Parameters.AddWithValue("u", _dmUserId);
            cmd.ExecuteNonQuery();
            return new { beats = new object[] { new { title = "ฉากหนึ่ง", summary = "x" } } };
        }));

        var response = await UploadAsync(_dmToken, _campaignId, ("a.pdf", "application/pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("quota_exceeded", (await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions))!.Error);
        await using var check = await OpenAsync();
        Assert.Equal(990L, await ScalarAsync<long>(check,
            "SELECT used_bytes FROM storage_quotas WHERE user_id = @u", ("u", _dmUserId)));
        Assert.Equal(0L, await ScalarAsync<long>(check,
            "SELECT count(*) FROM source_documents WHERE campaign_id = @c", ("c", _campaignId)));
    }

    // ── ด่านตรวจ (approve) ─────────────────────────────────────────────────

    [Fact]
    public async Task Player_cannot_approve_a_beat()
    {
        var response = await AuthedClient(_playerToken).PostAsync(
            $"/campaigns/{_campaignId}/beats/{_existingBeatId}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dm_cannot_approve_a_beat_of_another_campaign_through_their_own()
    {
        var response = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/beats/{_otherCampaignPendingBeatId}/approve", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var conn = await OpenAsync();
        Assert.Equal("Pending", await ScalarAsync<string>(conn,
            "SELECT review_status FROM story_beats WHERE id = @id", ("id", _otherCampaignPendingBeatId)));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task AssertNothingSavedAsync()
    {
        await using var conn = await OpenAsync();
        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM source_documents WHERE campaign_id = @c", ("c", _campaignId)));
        Assert.Equal(1L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM story_beats WHERE campaign_id = @c", ("c", _campaignId)));
        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT coalesce(sum(used_bytes), 0)::bigint FROM storage_quotas WHERE user_id = @u", ("u", _dmUserId)));
    }

    private async Task AskAssistantAsync()
    {
        (await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = "ตอนนี้ถึงไหนแล้ว" })).EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> UploadAsync(
        string token, Guid campaignId, params (string FileName, string ContentType, byte[] Bytes)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (fileName, contentType, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(part, "files", fileName);
        }

        return AuthedClient(token).PostAsync($"/campaigns/{campaignId}/story-imports", content);
    }

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId) =>
        await ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @owner, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", id), ("owner", ownerUserId), ("name", $"StoryImport {_suffix}"));

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role) =>
        await ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @c, @u, @role, now())",
            ("id", Guid.NewGuid()), ("c", campaignId), ("u", userId), ("role", role));

    private static async Task InsertBeatAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, int order, string title, string reviewStatus) =>
        await ExecAsync(conn,
            """
            INSERT INTO story_beats
                (id, campaign_id, "order", title, exit_conditions, monster_refs, npc_refs, secrets, progress, review_status)
            VALUES
                (@id, @c, @order, @title, ARRAY[]::text[], ARRAY[]::uuid[], ARRAY[]::uuid[], '[]', 'Upcoming', @status)
            """,
            ("id", id), ("c", campaignId), ("order", order), ("title", title), ("status", reviewStatus));

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"story-import-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"StoryImport {label}",
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

    public async Task DisposeAsync()
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)",
            ("ids", new[] { _campaignId, _otherCampaignId, _keylessCampaignId }));
        var users = new[] { _dmUserId, _playerUserId, _outsiderUserId, _keylessDmUserId };
        await ExecAsync(conn, "DELETE FROM storage_quotas WHERE user_id = ANY(@ids)", ("ids", users));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", users));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record ErrorBody(string Error);

    private sealed record BeatItem(
        Guid Id, int Order, string Title, string? Summary, string? Objective, string? EntryTrigger,
        string[] ExitConditions, string? DmNotes, string Secrets, string Progress, string ReviewStatus);

    private sealed record DocumentItem(Guid Id, string FileName);

    private sealed record ImportResponse(List<DocumentItem> Documents, List<BeatItem> Beats);
}

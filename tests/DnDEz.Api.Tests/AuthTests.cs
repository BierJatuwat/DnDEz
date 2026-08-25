using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint A Part 2 — lightweight custom auth (PasswordHasher&lt;User&gt; + JWT bearer, ไม่ใช่
/// ASP.NET Core Identity เต็มรูป — ดูเหตุผลใน docs/PROJECT-CONTEXT.md §4 "Sprint A Part 2")
///
/// contract ที่ implementation (green) ต้องทำตามให้ตรง:
///   POST /auth/register  body {Email,Password,DisplayName} (JSON property ไม่สนตัวพิมพ์)
///     -> success status code (2xx) + JSON { Token, UserId } เมื่อสำเร็จ
///     -> ต้อง insert แถวใหม่เข้า users ผ่าน role dndez_app จริง (password_hash เป็น hash จริง ไม่ใช่ plaintext)
///   POST /auth/login     body {Email,Password}
///     -> 200 OK + JSON { Token, UserId } เมื่อ credential ถูก
///     -> 401 เมื่อ email ไม่มีในระบบ หรือ password ผิด — **response body ต้องเหมือนกันเป๊ะทั้งสองกรณี**
///        (กัน user enumeration — เทสต์นี้เทียบ string ตรง ๆ)
///   endpoint ที่มี [Authorize] (ตัวแทนคือ /campaigns/{id}/diagnostics/whoami จาก
///     CrossCampaignRlsMiddlewareTests.cs) -> ไม่มี header Authorization หรือ token ไม่ valid -> 401
///
/// ห้าม mock ใด ๆ ต่อ Postgres จริงที่ docker compose รันอยู่เท่านั้น (เหมือน RlsFailClosedTests.cs)
/// </summary>
public class AuthTests : IAsyncLifetime
{
    // owner role — ใช้ตรวจ/เคลียร์ข้อมูลทดสอบเท่านั้น ไม่ใช่ทางที่แอปใช้จริง
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly string _uniqueSuffix = Guid.NewGuid().ToString("N");
    private readonly List<string> _emailsToCleanUp = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_emailsToCleanUp.Count > 0)
        {
            await using var conn = new NpgsqlConnection(OwnerConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM users WHERE email = ANY(@emails)", conn);
            cmd.Parameters.AddWithValue("emails", _emailsToCleanUp.ToArray());
            await cmd.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    private string UniqueEmail(string label) => $"{label}-{_uniqueSuffix}@example.test";

    [Fact]
    public async Task Register_then_login_with_same_credentials_returns_usable_jwt()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail("register-login");
        _emailsToCleanUp.Add(email);

        var registerResponse = await client.PostAsJsonAsync("/auth/register",
            new { Email = email, Password = "correct-horse-battery-staple", DisplayName = "Sprint A Tester" });

        Assert.True(registerResponse.IsSuccessStatusCode,
            $"register expected success, got {registerResponse.StatusCode}: {await registerResponse.Content.ReadAsStringAsync()}");

        // ยืนยันว่า insert ผ่าน dndez_app ติดจริงใน DB จริง (ไม่ใช่แค่ endpoint คืน 200 เฉย ๆ) และเป็น hash จริง
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT password_hash FROM users WHERE email = @email", conn);
            cmd.Parameters.AddWithValue("email", email);
            var hash = (string?)await cmd.ExecuteScalarAsync();
            Assert.False(string.IsNullOrWhiteSpace(hash));
            Assert.NotEqual("correct-horse-battery-staple", hash);
        }

        var loginResponse = await client.PostAsJsonAsync("/auth/login",
            new { Email = email, Password = "correct-horse-battery-staple" });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var body = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.NotEqual(Guid.Empty, body.UserId);
    }

    [Fact]
    public async Task Login_with_wrong_password_and_login_with_unknown_email_return_401_with_identical_body()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail("wrong-pw");
        _emailsToCleanUp.Add(email);

        var registerResponse = await client.PostAsJsonAsync("/auth/register",
            new { Email = email, Password = "correct-horse-battery-staple", DisplayName = "Wrong PW Tester" });
        Assert.True(registerResponse.IsSuccessStatusCode);

        var wrongPasswordResponse = await client.PostAsJsonAsync("/auth/login",
            new { Email = email, Password = "not-the-right-password" });
        var unknownEmailResponse = await client.PostAsJsonAsync("/auth/login",
            new { Email = UniqueEmail("does-not-exist"), Password = "whatever-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmailResponse.StatusCode);

        var wrongPasswordBody = await wrongPasswordResponse.Content.ReadAsStringAsync();
        var unknownEmailBody = await unknownEmailResponse.Content.ReadAsStringAsync();

        // กัน user enumeration ผ่านข้อความ error ที่ต่างกันระหว่างสองเคส
        Assert.Equal(wrongPasswordBody, unknownEmailBody);
    }

    // --- code review round 1 finding: catch (DbUpdateException) กว้างเกินไป ยืนยันเชิงประจักษ์แล้วว่า
    // overlong DisplayName ถูกจัดผิดเป็น "email_already_registered" ทั้งที่ไม่เกี่ยวข้อง — ต้องแยกให้ถูก
    // ด้วย PostgresException.SqlState == "23505" (unique_violation) เท่านั้น อย่างอื่นต้อง rethrow

    [Fact]
    public async Task Register_duplicate_email_returns_conflict_and_does_not_break_the_original_account()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail("duplicate-email");
        _emailsToCleanUp.Add(email);

        var firstResponse = await client.PostAsJsonAsync("/auth/register",
            new { Email = email, Password = "original-password-123", DisplayName = "First Registration" });
        Assert.True(firstResponse.IsSuccessStatusCode,
            $"first register expected success, got {firstResponse.StatusCode}");

        var duplicateResponse = await client.PostAsJsonAsync("/auth/register",
            new { Email = email, Password = "different-password-456", DisplayName = "Second Attempt" });

        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        var errorBody = await duplicateResponse.Content.ReadFromJsonAsync<AuthErrorResponse>(JsonOptions);
        Assert.Equal("email_already_registered", errorBody?.Error);

        // savepoint ต้อง rollback เฉพาะ insert ที่ชน ไม่ทำให้ request นี้ (หรือ user เดิม) เสียหาย —
        // ยืนยันว่า user ตัวแรกยัง login ด้วยรหัสผ่านเดิมได้ปกติหลังจากนั้น
        var loginResponse = await client.PostAsJsonAsync("/auth/login",
            new { Email = email, Password = "original-password-123" });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task Register_with_overlong_display_name_does_not_get_misclassified_as_duplicate_email()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail("overlong-displayname");
        // ไม่ใส่เข้า _emailsToCleanUp เพราะ insert ต้อง fail สนิท ไม่มีแถวให้ลบ (เทสต์ด้านล่างยืนยันซ้ำ)

        // display_name column เป็น varchar(100) ตาม InitialSchema migration — 101 ตัวอักษรต้อง fail
        // ด้วย string-data-right-truncation ของ Postgres ไม่ใช่ unique_violation
        var overlongDisplayName = new string('x', 101);

        var response = await client.PostAsJsonAsync("/auth/register",
            new { Email = email, Password = "some-valid-password", DisplayName = overlongDisplayName });

        // error ที่ไม่ใช่ unique-violation ต้อง rethrow ตาม fix (ดู Program.cs) กลายเป็น 500 unhandled
        // exception ตามปกติ — assert status code ตรง ๆ ไม่ใช่แค่ "ไม่ใช่ 409" เพื่อกันเทสต์ผ่านทั้งที่
        // endpoint คืน status code อื่นที่ไม่ควรเกิด (ตามข้อเสนอแนะจาก code review รอบ 2)
        // อ่านเป็น string ดิบ ไม่ deserialize เป็น AuthErrorResponse เพราะ 500 unhandled exception
        // ไม่การันตีว่า body เป็น JSON รูปแบบเดียวกับ error response ปกติของเรา
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("email_already_registered", rawBody);

        // ไม่มีแถวใดถูก insert จริงสำหรับ email นี้ ไม่ว่า endpoint จะตอบ status อะไรก็ตาม —
        // ข้อมูลที่ผิด constraint ต้องไม่หลุดเข้า DB แม้บางส่วน
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM users WHERE email = @email", conn);
        cmd.Parameters.AddWithValue("email", email);
        var rowCount = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task Protected_endpoint_without_jwt_returns_401()
    {
        var client = _factory.CreateClient();

        // ควรโดนบล็อกที่ authorization ก่อนถึงชั้น DB ด้วยซ้ำ ใช้ guid สุ่มได้เลย
        var response = await client.GetAsync($"/campaigns/{Guid.NewGuid()}/diagnostics/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_with_malformed_bearer_token_returns_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "this-is-not-a-real-jwt");

        var response = await client.GetAsync($"/campaigns/{Guid.NewGuid()}/diagnostics/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record AuthErrorResponse(string Error);
}

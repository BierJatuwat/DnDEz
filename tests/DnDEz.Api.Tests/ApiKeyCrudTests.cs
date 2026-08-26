using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint D Part 1 — BYOK: เก็บ/ดึง/ลบ API key ผู้ใช้แบบเข้ารหัสจริงผ่าน ASP.NET Data Protection
/// (persist ลง Redis จริง — ดู Program.cs) ต่อ RLS `own_rows_only` ที่มีอยู่แล้วตั้งแต่ InitialSchema
///
/// ขอบเขต (ดู PROJECT-CONTEXT §4 "Sprint D" Part 1):
///  • ไม่มี live validation เรียก Anthropic จริง — key ใหม่ทุกตัวเริ่มที่ Active เสมอ (optimistic)
///  • รองรับแค่ provider "anthropic" เท่านั้นตอนนี้
///
/// contract:
///
/// PUT /users/me/api-keys/{provider}  [Authorize]
///   body { ApiKey (required, >= 20 ตัวอักษรหลัง trim) }
///   200 OK + { Provider, KeySuffix, Status, CreatedAt, LastValidatedAt } — **ไม่มี key ใด ๆ ในนี้เลย**
///   400 เมื่อ provider ไม่รองรับ หรือ ApiKey ว่าง/สั้นเกินไป — ต้องไม่แตะ DB เลยตอน reject
///   upsert จริง: ยิงซ้ำ provider เดิม = rotate (แถวเดิม ไม่ใช่แถวใหม่)
///
/// GET /users/me/api-keys  [Authorize]
///   200 OK + [{ Provider, KeySuffix, Status, CreatedAt, LastValidatedAt }] ของ user ปัจจุบันเท่านั้น
///
/// DELETE /users/me/api-keys/{provider}  [Authorize]
///   204 No Content เสมอ (idempotent ไม่ว่ามีอยู่จริงหรือไม่)
///
/// ห้าม mock Data Protection/Postgres — เข้ารหัส/ถอดรหัสจริงผ่าน Redis จริงที่ docker compose รันอยู่
/// </summary>
public class ApiKeyCrudTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "api-key-crud-secret-1";
    private const string ProtectorPurpose = "DnDEz.ApiKeys.v1";
    private const string ValidRawKey = "sk-ant-api03-test-valid-key-1234567890";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _userId;
    private string _token = null!;
    private Guid _otherUserId;
    private string _otherToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_userId, _token) = await RegisterAsync("owner");
        (_otherUserId, _otherToken) = await RegisterAsync("other");
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var userIds = new[] { _userId, _otherUserId }.Where(id => id != Guid.Empty).ToArray();
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
            Email = $"api-key-crud-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"ApiKey CRUD {label}",
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

    private async Task<long> CountApiKeyRowsAsync(Guid userId, string provider)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM api_keys WHERE user_id = @userId AND provider = @provider", conn);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("provider", provider);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(Guid Id, string EncryptedKey)> ReadRawRowAsync(Guid userId, string provider)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, encrypted_key FROM api_keys WHERE user_id = @userId AND provider = @provider", conn);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("provider", provider);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetGuid(0), reader.GetString(1));
    }

    private string DecryptViaRealDataProtector(string encryptedKey)
    {
        var provider = _factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = provider.CreateProtector(ProtectorPurpose);
        return protector.Unprotect(encryptedKey);
    }

    // ── happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Put_api_key_returns_200_with_metadata_only_no_key_material()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ValidRawKey, rawJson);
        Assert.DoesNotContain("encryptedKey", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);

        var body = JsonSerializer.Deserialize<ApiKeyResponse>(rawJson, JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("anthropic", body!.Provider);
        Assert.Equal("7890", body.KeySuffix); // ท้าย 4 หลักของ ValidRawKey
        Assert.Equal("Active", body.Status);
        Assert.Null(body.LastValidatedAt);
    }

    [Fact]
    public async Task Put_api_key_encrypts_value_at_rest_and_round_trips_correctly()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (_, encryptedKey) = await ReadRawRowAsync(_userId, "anthropic");
        Assert.NotEqual(ValidRawKey, encryptedKey); // ต้องไม่ใช่ plaintext ใน DB เด็ดขาด
        Assert.DoesNotContain(ValidRawKey, encryptedKey);

        var decrypted = DecryptViaRealDataProtector(encryptedKey);
        Assert.Equal(ValidRawKey, decrypted); // round-trip ผ่าน Data Protection จริง (ไม่ mock)
    }

    /// <summary>
    /// พบระหว่าง code review — เทสต์ round-trip ด้านบนถอดรหัสผ่าน `_factory` ตัวเดียวกับที่เข้ารหัส
    /// ซึ่ง .NET DataProtection cache key ring ไว้ใน memory ตลอดอายุ DI container อยู่แล้ว จึงผ่านได้
    /// แม้ PersistKeysToStackExchangeRedis จะพังหรือชี้ผิด Redis ก็ตาม (ไม่ได้พิสูจน์ Redis เลยจริง ๆ)
    /// — เทสต์นี้เปิด `WebApplicationFactory` "ตัวที่สอง" แยกอิสระ (จำลอง process/instance ใหม่ที่
    /// ไม่มี key ring ใน memory ของตัวเองมาก่อนเลย) มา decrypt ciphertext ที่ตัวแรกเข้ารหัสไว้ —
    /// ผ่านได้แปลว่า key ring ถูกแชร์ผ่าน Redis จริง ไม่ใช่แค่ shared memory ของ process เดียวกัน
    /// </summary>
    [Fact]
    public async Task Put_api_key_ciphertext_decrypts_from_a_second_independent_app_instance_via_redis()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (_, encryptedKey) = await ReadRawRowAsync(_userId, "anthropic");

        await using var secondFactory = new WebApplicationFactory<Program>();
        var secondProvider = secondFactory.Services.GetRequiredService<IDataProtectionProvider>();
        var secondProtector = secondProvider.CreateProtector(ProtectorPurpose);

        var decrypted = secondProtector.Unprotect(encryptedKey);
        Assert.Equal(ValidRawKey, decrypted);
    }

    [Fact]
    public async Task Put_api_key_two_different_users_same_provider_persist_independently()
    {
        const string otherUsersKey = "sk-ant-api03-other-user-own-key-11223344";

        var ownResponse = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });
        var otherResponse = await AuthedClient(_otherToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = otherUsersKey });

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);

        // unique constraint เป็น composite (user_id, provider) ไม่ใช่ provider เดี่ยว ๆ — สอง user
        // ใช้ provider เดียวกันต้องอยู่ร่วมกันได้ปกติ ไม่ชนกัน
        Assert.Equal(1, await CountApiKeyRowsAsync(_userId, "anthropic"));
        Assert.Equal(1, await CountApiKeyRowsAsync(_otherUserId, "anthropic"));

        var (_, ownEncrypted) = await ReadRawRowAsync(_userId, "anthropic");
        var (_, otherEncrypted) = await ReadRawRowAsync(_otherUserId, "anthropic");
        Assert.Equal(ValidRawKey, DecryptViaRealDataProtector(ownEncrypted));
        Assert.Equal(otherUsersKey, DecryptViaRealDataProtector(otherEncrypted));
    }

    [Fact]
    public async Task Put_api_key_two_concurrent_requests_race_one_succeeds_other_gets_ok_or_conflict()
    {
        // simulate race: ทั้งสอง request เห็น "ยังไม่มีแถว" พร้อมกัน (ครั้งแรกของ user นี้เสมอ เพราะ
        // เพิ่งลงทะเบียนใน InitializeAsync ไม่เคย PUT มาก่อน) ยิงพร้อมกันจริงผ่าน Task.WhenAll —
        // unique constraint ที่ DB ต้องกันไม่ให้เกิด 2 แถว ไม่ว่า response ทั้งคู่จะเป็นอะไรก็ตาม
        var client = AuthedClient(_token);
        var task1 = client.PutAsJsonAsync("/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });
        var task2 = client.PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = "sk-ant-api03-second-racer-key-5566778899" });

        var responses = await Task.WhenAll(task1, task2);

        // ต้องไม่มีใครได้ 500 เลย (either 200 OK ปกติ หรือ 409 Conflict ถ้าแพ้ race) — ไม่มี unhandled
        // DbUpdateException หลุดออกมาเป็น 500 ดิบ ๆ (บั๊กที่ code review เจอและแก้แล้ว)
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"คาดว่าเป็น 200 หรือ 409 แต่ได้ {r.StatusCode}"));

        // ไม่ว่าผลจะออกมาแบบไหน ท้ายที่สุดต้องมีแถวเดียวสำหรับ (user, provider) นี้เท่านั้น
        Assert.Equal(1, await CountApiKeyRowsAsync(_userId, "anthropic"));
    }

    [Fact]
    public async Task Put_api_key_with_different_case_provider_returns_400()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/Anthropic", new { ApiKey = ValidRawKey });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountApiKeyRowsAsync(_userId, "Anthropic"));
    }

    [Fact]
    public async Task Put_api_key_twice_rotates_same_row_not_duplicate()
    {
        var first = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var (firstRowId, _) = await ReadRawRowAsync(_userId, "anthropic");

        const string rotatedKey = "sk-ant-api03-rotated-key-9876543210";
        var second = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = rotatedKey });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(1, await CountApiKeyRowsAsync(_userId, "anthropic"));
        var (secondRowId, encryptedKey) = await ReadRawRowAsync(_userId, "anthropic");
        Assert.Equal(firstRowId, secondRowId); // rotate แถวเดิม ไม่สร้างแถวใหม่
        Assert.Equal(rotatedKey, DecryptViaRealDataProtector(encryptedKey));

        var secondBody = await second.Content.ReadFromJsonAsync<ApiKeyResponse>(JsonOptions);
        Assert.Equal("3210", secondBody!.KeySuffix);
    }

    // ── validation (400 — ห้ามแตะ DB เลย) ───────────────────────────────

    [Fact]
    public async Task Put_api_key_with_unsupported_provider_returns_400_and_creates_no_row()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/openai", new { ApiKey = ValidRawKey });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountApiKeyRowsAsync(_userId, "openai"));
    }

    [Fact]
    public async Task Put_api_key_too_short_returns_400_and_creates_no_row()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountApiKeyRowsAsync(_userId, "anthropic"));
    }

    /// <summary>พบระหว่าง security review — เดิมไม่มี upper bound เลย ทั้งชั้น API และ DB
    /// (`encrypted_key` เป็น `text` ไม่จำกัดความยาว) เป็นช่องทาง resource exhaustion เล็ก ๆ
    /// สำหรับ user ที่ authenticate แล้ว</summary>
    [Fact]
    public async Task Put_api_key_too_long_returns_400_and_creates_no_row()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = new string('a', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountApiKeyRowsAsync(_userId, "anthropic"));
    }

    [Fact]
    public async Task Put_api_key_with_empty_body_returns_400()
    {
        var response = await AuthedClient(_token).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── GET / list ──────────────────────────────────────────────────────

    [Fact]
    public async Task Get_api_keys_with_none_set_returns_empty_array()
    {
        var response = await AuthedClient(_otherToken).GetAsync("/users/me/api-keys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<ApiKeyResponse>>(JsonOptions);
        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task Get_api_keys_only_returns_current_users_keys_not_other_users()
    {
        await AuthedClient(_token).PutAsJsonAsync("/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });

        var ownResponse = await AuthedClient(_token).GetAsync("/users/me/api-keys");
        var ownBody = await ownResponse.Content.ReadFromJsonAsync<List<ApiKeyResponse>>(JsonOptions);
        Assert.Single(ownBody!);

        var otherResponse = await AuthedClient(_otherToken).GetAsync("/users/me/api-keys");
        var otherBody = await otherResponse.Content.ReadFromJsonAsync<List<ApiKeyResponse>>(JsonOptions);
        Assert.Empty(otherBody!); // RLS + WHERE ทั้งคู่ต้องกันไขว้ user กันจริง
    }

    /// <summary>เช็คเดียวกับ PUT_returns_200_with_metadata_only ด้านบน แต่ฝั่ง GET — เพื่อความสมมาตร
    /// ตามที่ code review เสนอ (เดิมมีแค่ PUT ที่ค้น raw JSON ตรง ๆ)</summary>
    [Fact]
    public async Task Get_api_keys_response_never_contains_key_material()
    {
        await AuthedClient(_token).PutAsJsonAsync("/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });

        var response = await AuthedClient(_token).GetAsync("/users/me/api-keys");
        var rawJson = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(ValidRawKey, rawJson);
        Assert.DoesNotContain("encryptedKey", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ── DELETE ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_api_key_removes_row_and_get_no_longer_lists_it()
    {
        await AuthedClient(_token).PutAsJsonAsync("/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });
        Assert.Equal(1, await CountApiKeyRowsAsync(_userId, "anthropic"));

        var deleteResponse = await AuthedClient(_token).DeleteAsync("/users/me/api-keys/anthropic");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.Equal(0, await CountApiKeyRowsAsync(_userId, "anthropic"));
        var getResponse = await AuthedClient(_token).GetAsync("/users/me/api-keys");
        var body = await getResponse.Content.ReadFromJsonAsync<List<ApiKeyResponse>>(JsonOptions);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task Delete_api_key_that_does_not_exist_returns_204_idempotent()
    {
        var response = await AuthedClient(_otherToken).DeleteAsync("/users/me/api-keys/anthropic");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── authorization ───────────────────────────────────────────────────

    [Fact]
    public async Task Put_api_key_without_jwt_returns_401()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = ValidRawKey });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_api_keys_without_jwt_returns_401()
    {
        var response = await _factory.CreateClient().GetAsync("/users/me/api-keys");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_api_key_without_jwt_returns_401()
    {
        var response = await _factory.CreateClient().DeleteAsync("/users/me/api-keys/anthropic");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record ApiKeyResponse(
        string Provider,
        string KeySuffix,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastValidatedAt);
}

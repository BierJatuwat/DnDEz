using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint D Part 1 — BYOK: เก็บ/ดึง/ลบ API key ของผู้ใช้แบบเข้ารหัส (`dm-side-design.md` §7)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/ApiKeyCrudTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// ขอบเขต:
///  • Sprint D **Part 2** เติม live validation กลับเข้ามาแล้ว ("Validate ด้วย test call ทันทีตอนกรอก"
///    ตาม dm-side-design.md §7) — เรียก `IAnthropicClient.ValidateKeyAsync` ก่อนบันทึกเสมอ
///    `Valid`→`ApiKeyStatus.Active`, `Invalid`→`ApiKeyStatus.Invalid` (แต่ยังบันทึก key ไว้ ไม่ reject
///    ทั้ง request — ผู้ใช้เห็นสถานะแล้วแก้เองได้ ไม่ต้องพิมพ์ซ้ำ), `Unknown` (Anthropic ล่มชั่วคราว/
///    network error) → **คงสถานะเดิมไว้** ไม่ตีเป็น Invalid เด็ดขาด (กัน Anthropic ล่มชั่วคราวทำให้ key
///    ที่ใช้ได้จริงถูกตราว่าใช้ไม่ได้) แต่ key ใหม่ (ไม่เคยมีแถวมาก่อน) ที่เจอ Unknown จะได้ Active
///    default ไปก่อน (optimistic เหมือน Part 1 เดิม เพราะไม่มี "สถานะเดิม" ให้คงไว้)
///  • รองรับแค่ `provider = "anthropic"` เท่านั้นตอนนี้ (`SupportedProviders`) — ค่าอื่นได้ 400
///
/// จุดที่ห้ามพลาด:
///  • **ห้าม log/return ค่า key จริงหรือ `EncryptedKey` เด็ดขาดทุก endpoint** (ล็อกไว้ใน
///    dm-side-design.md §7: "ไม่ log ค่า key เต็มที่ไหนเลย") — response DTO ไม่มี property นี้ตั้งแต่ต้น
///    การันตีด้วย type system เหมือนกฎเดียวกับ Seed ใน DiceRollEndpoints
///  • เข้ารหัสด้วย `IDataProtectionProvider.CreateProtector` เท่านั้น ห้ามเก็บ plaintext ลง DB แม้แต่
///    ชั่วขณะ (ไม่มี debug log ของ raw key ระหว่างทาง)
///  • RLS ของ `api_keys` (`own_rows_only`, ผูกกับ `app.user_id`) เป็นเส้นสุดท้ายที่กันข้ามยูสเซอร์อยู่
///    แล้ว แต่ query ยังต้อง filter ด้วย `UserId == principal.GetUserId()` เองด้วย (defense-in-depth
///    เหมือน pattern อื่นในระบบนี้ที่เช็คสองชั้นแม้ RLS fail-closed ได้จริง)
///  • `(user_id, provider)` unique ที่ DB (`AddApiKeysConstraints`) — endpoint ต้อง query แถวเดิมมา
///    ก่อนเสมอแล้ว update ถ้าเจอ ไม่ใช่ insert ใหม่ทุกครั้ง (ไม่งั้นชน unique constraint กลายเป็น 500
///    แทนที่จะเป็นการ rotate key ตามที่ตั้งใจ)
/// </summary>
public static class ApiKeyEndpoints
{
    private const string ProtectorPurpose = "DnDEz.ApiKeys.v1";
    private const int MinApiKeyLength = 20;
    // real Anthropic key format ไม่เกินนี้แน่ ๆ (มีระยะเผื่อเยอะ) — พบตอน security review ว่าเดิม
    // ไม่มี upper bound เลยทั้งชั้น API และ DB (`encrypted_key` เป็น `text` ไม่จำกัดความยาว) แม้แต่
    // user ที่ authenticate แล้วก็ยังยัด payload ใหญ่ผิดปกติซ้ำ ๆ ได้ (resource exhaustion เล็ก ๆ)
    private const int MaxApiKeyLength = 200;
    private const int KeySuffixLength = 4;

    /// <summary>provider ที่รองรับตอนนี้ — เพิ่มได้ทีหลังเมื่อมี HTTP client ของ provider นั้นจริง
    /// (Voyage/OpenAI ถูกพูดถึงใน PROJECT-CONTEXT §4 สำหรับ embeddings แต่ยังไม่ถึงตอนนี้)</summary>
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.Ordinal) { "anthropic" };

    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        app.MapPut("/users/me/api-keys/{provider}", async (
            string provider,
            SetApiKeyRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient) =>
        {
            if (!SupportedProviders.Contains(provider))
            {
                return Results.BadRequest();
            }

            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return Results.BadRequest();
            }

            var keyLength = request.ApiKey.Trim().Length;
            if (keyLength < MinApiKeyLength || keyLength > MaxApiKeyLength)
            {
                return Results.BadRequest();
            }

            var userId = principal.GetUserId();
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var rawKey = request.ApiKey.Trim();

            // validate ก่อนแตะ DB เสมอ (เหมือนหลักอื่นทั้งระบบนี้ที่ validate แบบ pure ก่อนเขียนอะไร)
            // — ตอนนี้รองรับ provider เดียว ("anthropic") จึงเรียก validator เดียวได้ตรง ๆ ถ้าเพิ่ม
            // provider อื่นทีหลังต้องแตกเป็น validator ต่อ provider แทน
            var validationOutcome = await anthropicClient.ValidateKeyAsync(rawKey, CancellationToken.None);

            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            var encryptedKey = protector.Protect(rawKey);
            var keySuffix = rawKey.Length <= KeySuffixLength ? rawKey : rawKey[^KeySuffixLength..];

            var existing = await db.ApiKeys
                .SingleOrDefaultAsync(k => k.UserId == userId.Value && k.Provider == provider);

            // Unknown (Anthropic ล่มชั่วคราว/network) ต้องไม่ตีเป็น Invalid — คงสถานะเดิมไว้ถ้ามีแถวอยู่
            // แล้ว, ถ้าเป็น key ใหม่ (ไม่มี "สถานะเดิม" ให้คง) fallback เป็น Active แบบ optimistic
            var (status, lastValidatedAt) = validationOutcome switch
            {
                ApiKeyValidationOutcome.Valid => (ApiKeyStatus.Active, (DateTimeOffset?)DateTimeOffset.UtcNow),
                ApiKeyValidationOutcome.Invalid => (ApiKeyStatus.Invalid, (DateTimeOffset?)DateTimeOffset.UtcNow),
                _ => (existing?.Status ?? ApiKeyStatus.Active, existing?.LastValidatedAt),
            };

            ApiKey apiKey;
            if (existing is null)
            {
                apiKey = new ApiKey
                {
                    Id = Guid.NewGuid(),
                    UserId = userId.Value,
                    Provider = provider,
                    EncryptedKey = encryptedKey,
                    KeySuffix = keySuffix,
                    Status = status,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastValidatedAt = lastValidatedAt,
                };
                db.ApiKeys.Add(apiKey);
            }
            else
            {
                // rotate: ค่า key เปลี่ยนแล้ว validation ผลใหม่ (คำนวณจาก key ใหม่ด้านบนแล้ว) ใช้แทน
                existing.EncryptedKey = encryptedKey;
                existing.KeySuffix = keySuffix;
                existing.Status = status;
                existing.LastValidatedAt = lastValidatedAt;
                apiKey = existing;
            }

            // race: สอง PUT พร้อมกันของ (userId, provider) เดียวกันอาจเห็น existing=null ทั้งคู่แล้ว
            // Add สองแถวพร้อมกัน — unique constraint (AddApiKeysConstraints) กันไม่ให้ทั้งคู่ผ่านจริง
            // แต่ถ้าไม่จับ exception ไว้ request ที่แพ้ race จะได้ 500 ดิบ ๆ — savepoint pattern
            // เดียวกับ /auth/register (Program.cs) เพราะ insert นี้อยู่ใน transaction เดียวกับทั้ง
            // request อยู่แล้ว ถ้าปล่อยให้ error ลอยจะทำให้ commit ท้าย request พังไปด้วย
            var currentTransaction = db.Database.CurrentTransaction;
            const string savepoint = "set_api_key";
            if (currentTransaction is not null)
            {
                await currentTransaction.CreateSavepointAsync(savepoint);
            }

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                if (currentTransaction is not null)
                {
                    await currentTransaction.RollbackToSavepointAsync(savepoint);
                }

                db.ChangeTracker.Clear();

                if (ex.InnerException is not PostgresException { SqlState: "23505" })
                {
                    throw;
                }

                // แพ้ race ให้ client ยิงซ้ำเอง (idempotent อยู่แล้ว — PUT ซ้ำ = rotate ปกติ)
                return Results.Conflict();
            }

            return Results.Ok(ToResponse(apiKey));
        }).RequireAuthorization();

        app.MapGet("/users/me/api-keys", async (
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var keys = await db.ApiKeys
                .Where(k => k.UserId == userId.Value)
                .OrderBy(k => k.Provider)
                .Select(k => new ApiKeyResponse(k.Provider, k.KeySuffix, k.Status.ToString(), k.CreatedAt, k.LastValidatedAt))
                .ToListAsync();

            return Results.Ok(keys);
        }).RequireAuthorization();

        app.MapDelete("/users/me/api-keys/{provider}", async (
            string provider,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var existing = await db.ApiKeys
                .SingleOrDefaultAsync(k => k.UserId == userId.Value && k.Provider == provider);

            // idempotent เหมือน DELETE ทั่วไป — ไม่มีอยู่แล้วก็ถือว่า "ลบสำเร็จ" ไม่ต้อง 404
            if (existing is not null)
            {
                db.ApiKeys.Remove(existing);
                await db.SaveChangesAsync();
            }

            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static ApiKeyResponse ToResponse(ApiKey apiKey) =>
        new(apiKey.Provider, apiKey.KeySuffix, apiKey.Status.ToString(), apiKey.CreatedAt, apiKey.LastValidatedAt);
}

internal record SetApiKeyRequest(string? ApiKey);

/// <summary>ไม่มี EncryptedKey/plaintext ในนี้เด็ดขาด — ตั้งใจตั้งแต่ตอนออกแบบ DTO</summary>
internal record ApiKeyResponse(
    string Provider,
    string KeySuffix,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastValidatedAt);

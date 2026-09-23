using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint E Part 3 — player action input (hybrid buttons + free text). ทั้งสองทางส่ง content
/// เดียวกันมาที่ endpoint นี้ (ดู `player-side-design.md` §2 — ปุ่มเป็นแค่ template ข้อความ ไม่ใช่
/// เส้นทางแยก) ไหลเข้า <see cref="OrchestratorTurnRunner"/> ตาม PROJECT-CONTEXT §2: "Action ของผู้เล่น
/// ทุกอันไหลผ่าน rules engine และ approval gate เดิม"
///
/// **auth ไม่ใช่ DM-only** — ผู้เล่นโพสต์ action แทนตัวละครของตัวเองได้เท่านั้น (เช็ค
/// `Character.OwnerUserId == caller` ไม่ใช่ `Campaign.OwnerUserId == caller`) ตาม §6 ที่สงวนสิทธิ์
/// "เล่นแทนตัวละครคนอื่น" ไว้ให้ DM เท่านั้น
///
/// **API key ของ DM ต้องดึงผ่าน `app_get_dm_api_key()` (SECURITY DEFINER)** ไม่ใช่ query ตรง —
/// RLS `own_rows_only` ของ `api_keys` กรองด้วย `app.user_id` ของผู้เรียกจริง (ผู้เล่น) เสมอ ถ้า
/// query ตรงหา "key ของ DM" จะได้ NULL เสมอไม่ว่า C# WHERE จะเขียนถูกแค่ไหน — ดู migration
/// `AddDmApiKeyLookupFunction` สำหรับเหตุผลเต็มและ defense-in-depth ที่ใส่ไว้ในฟังก์ชันเอง
///
/// broadcast `ActionMessageCreated` เข้ากลุ่มแคมเปญทั้งกลุ่ม (ไม่ใช่ DM-only) — เนื้อหาเป็นของ
/// ผู้เล่นเองหรือ AI ตอบสนองการกระทำของผู้เล่นโดยตรง ปลอดภัยที่จะให้ทุกคนเห็นเสมอ (ต่างจากผู้ช่วย DM
/// ที่ DM คุยส่วนตัว ซึ่งมี pipeline แยกต่างหากใน `AssistantRunner` ไม่ผ่านไฟล์นี้เลย)
/// </summary>
public static class PlayerActionEndpoints
{
    private const string ProtectorPurpose = "DnDEz.ApiKeys.v1"; // ต้องตรงกับ ApiKeyEndpoints เป๊ะ
    private const string AnthropicProvider = "anthropic";
    private const int MaxContentLength = 4000;

    public static void MapPlayerActionEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/actions", async (
            Guid campaignId,
            Guid sessionId,
            PlayerActionRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient,
            IHubContext<CampaignHub> hubContext,
            ILoggerFactory loggerFactory) =>
        {
            // static class เป็น type argument ของ ILogger<T> ไม่ได้ (CS0718) — เหมือนทุก endpoint อื่น
            var logger = loggerFactory.CreateLogger(typeof(PlayerActionEndpoints));

            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.BadRequest();
            }

            var content = request.Content.Trim();
            if (content.Length > MaxContentLength)
            {
                return Results.BadRequest();
            }

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            // ผู้เล่นแทนตัวละครของตัวเองเท่านั้น — เล่นแทนคนอื่น/ตัวละคร quick-play (OwnerUserId=null)
            // สงวนไว้ให้ DM ผ่าน override เท่านั้น (§6) ยังไม่ทำในรอบนี้
            var (character, failure) = await CampaignAuthorization.RequireCharacterOwnerAsync(
                db, request.CharacterId, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            // EF Core's Database.SqlQuery<T> สำหรับ scalar type คาดหวัง column ชื่อ "Value" เป๊ะ
            // (มันคอมไพล์เป็น outer wrapper ที่ SELECT t."Value") — ต้อง alias เองเพราะฟังก์ชัน
            // Postgres คืนคอลัมน์ไม่มีชื่อ (unnamed) ตามปกติ
            var encryptedKey = await db.Database
                .SqlQuery<string>($"SELECT app_get_dm_api_key({campaignId}, {AnthropicProvider}) AS \"Value\"")
                .SingleOrDefaultAsync();
            if (encryptedKey is null)
            {
                return Results.BadRequest(new ConsultErrorResponse("dm_api_key_not_set"));
            }

            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            var rawApiKey = protector.Unprotect(encryptedKey);

            var result = await OrchestratorTurnRunner.RunTurnAsync(
                db, anthropicClient, hubContext, logger, campaign, sessionId, rawApiKey, content,
                callerUserId.Value, character!.Id);

            return Results.Ok(result);
        }).RequireAuthorization();
    }
}

internal record PlayerActionRequest(Guid CharacterId, string? Content);

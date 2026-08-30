using System.Security.Claims;
using System.Text.RegularExpressions;
using DnDEz.Api.Auth;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint F งานที่ 1 — join campaign flow ผ่านโค้ดที่ DM กำหนดเอง (ตัดสิน 2026-08-30 — ดู
/// PROJECT-CONTEXT §4 "Sprint F"): แทนที่จะเป็นโค้ดสุ่มแบบ CSPRNG ต่อ session (`GameSession.JoinCode`
/// เดิมที่ยังไม่เคยต่อ endpoint) โค้ดนี้ผูกกับ **campaign** และ **DM เป็นคนตั้งเอง** ใช้ join ได้ทั้ง
/// แคมเปญและทุก session ในนั้น (ไม่มี concept invite แยกจาก session/expiry/max-use แบบ Discord)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/CampaignMembershipEndpointsTests.cs</c>
///
/// จุดที่ห้ามพลาด:
///  • `POST /campaigns/join` ต้อง rate limit ต่อ user — เป็น enumeration surface ตรง ๆ (ลองพิมพ์โค้ด
///    สุ่มไปเรื่อย ๆ เพื่อหาแคมเปญที่มีอยู่จริง) ดูรูปแบบเดียวกับ `ApiKeyEndpoints.RateLimitPolicyName`
///  • code ไม่พบ = 404 เสมอ (ไม่แยกจาก "พบแต่หมดสิทธิ์" เพราะไม่มี concept นั้นในดีไซน์นี้) —
///    ตรงกับ convention ทั้งระบบว่าแถวที่มองไม่เห็น/ไม่มีอยู่จริงคืนโค้ดเดียวกัน กัน enumeration
///  • join ซ้ำ (เป็นสมาชิกอยู่แล้ว) เป็น no-op สำเร็จ ไม่ error — กัน DM/ผู้เล่นที่กด join ซ้ำเจอ 409/500
///    โดยไม่จำเป็น (ตัดสินใจไว้ใน `app_join_campaign()` เอง ดู migration `AddCampaignJoinCode`)
///  • set join code ต้องเช็คว่าเป็น DM (`campaign.OwnerUserId`) ก่อนเสมอ — ผ่าน RLS UPDATE policy
///    ใหม่ (`campaign_owner_update`) ที่เพิ่งเปิดใน migration เดียวกัน ไม่ใช่ SECURITY DEFINER
///    (ไม่มีปัญหา circular bootstrap แบบตอน join เพราะ DM เป็นสมาชิก/owner อยู่แล้วตอนจะแก้)
/// </summary>
public static class CampaignMembershipEndpoints
{
    public const string JoinRateLimitPolicyName = "campaign-join";

    // ตัวอักษร/ตัวเลข/ขีดกลาง/underscore เท่านั้น กันอักขระแปลก ๆ หลุดเข้า URL/QR — DM พิมพ์เอง
    // จึงไม่บังคับ entropy สูงเหมือน CSPRNG token แต่ยังกันสั้นเกินไปจนเดาง่าย (ขั้นต่ำ 4 ตัว)
    private static readonly Regex JoinCodePattern = new("^[A-Za-z0-9_-]{4,32}$", RegexOptions.Compiled);

    public static void MapCampaignMembershipEndpoints(this WebApplication app)
    {
        app.MapPut("/campaigns/{campaignId:guid}/join-code", async (
            Guid campaignId,
            SetJoinCodeRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
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

            if (campaign.OwnerUserId != callerUserId.Value)
            {
                return Results.Forbid();
            }

            string? normalizedCode = null;
            if (request.JoinCode is not null)
            {
                normalizedCode = request.JoinCode.Trim().ToUpperInvariant();
                if (!JoinCodePattern.IsMatch(normalizedCode))
                {
                    return Results.BadRequest();
                }
            }

            campaign.JoinCode = normalizedCode;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                // unique index ix_campaigns_join_code ชนกัน — แคมเปญอื่นใช้โค้ดนี้อยู่แล้ว
                return Results.Conflict(new JoinCodeErrorResponse("join_code_already_in_use"));
            }

            return Results.Ok(new SetJoinCodeResponse(campaign.Id, campaign.JoinCode));
        }).RequireAuthorization();

        app.MapPost("/campaigns/join", async (
            JoinCampaignRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.JoinCode))
            {
                return Results.BadRequest();
            }

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var normalizedCode = request.JoinCode.Trim().ToUpperInvariant();
            var membershipId = Guid.NewGuid();

            // EF Core's Database.SqlQuery<T> สำหรับ scalar type คาดหวัง column ชื่อ "Value" เป๊ะ
            // (เหมือน app_get_dm_api_key ใน PlayerActionEndpoints) — ฟังก์ชันคืน NULL แถวเดียว
            // เสมอถ้าไม่พบโค้ด ไม่ใช่ไม่มีแถวเลย จึง SingleOrDefaultAsync ได้ตรง ๆ
            var campaignId = await db.Database
                .SqlQuery<Guid?>($"SELECT app_join_campaign({normalizedCode}, {membershipId}) AS \"Value\"")
                .SingleOrDefaultAsync();

            if (campaignId is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new JoinCampaignResponse(campaignId.Value));
        }).RequireAuthorization().RequireRateLimiting(JoinRateLimitPolicyName);

        app.MapGet("/campaigns/{campaignId:guid}/members", async (
            Guid campaignId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var members = await db.CampaignMembers
                .Where(m => m.CampaignId == campaignId)
                .OrderBy(m => m.JoinedAt)
                .Select(m => new CampaignMemberResponse(
                    m.UserId, m.User.DisplayName, m.Role.ToString(), m.JoinedAt))
                .ToListAsync();

            return Results.Ok(members);
        }).RequireAuthorization();
    }
}

internal record SetJoinCodeRequest(string? JoinCode);

internal record SetJoinCodeResponse(Guid CampaignId, string? JoinCode);

internal record JoinCodeErrorResponse(string Error);

internal record JoinCampaignRequest(string? JoinCode);

internal record JoinCampaignResponse(Guid CampaignId);

internal record CampaignMemberResponse(Guid UserId, string DisplayName, string Role, DateTimeOffset JoinedAt);

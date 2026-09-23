using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// "บันทึกแคมเปญ" (Part D ของแผน "DM AI Assistant", 2026-09-22) — ชั้นความจำที่อยู่**ระหว่าง**
/// log ดิบของเซสชัน (`messages`, ยาวและเปลืองโทเค็นถ้าส่งเข้า AI ทั้งก้อน) กับ `recap_snapshots`
/// ที่สรุปสั้นจนรายละเอียดหาย
///
/// หลักการที่ผู้ใช้เลือกไว้: **ของที่ต้องรู้ตลอดยัดเข้า system prompt · ของที่นาน ๆ ใช้ทีให้ AI ค้นเอง**
/// — บันทึกจึงไม่ถูกยัดเข้า prompt แต่ AI ค้นผ่าน tool `search_campaign_journal`
/// (ดู `OrchestratorTurnRunner`) เมื่อ DM ถามอะไรที่ recap ตอบไม่ได้
///
/// **DM-only ทุก endpoint** — เนื้อหาบันทึกมีความลับของแคมเปญปนได้เสมอ โดยเฉพาะรายการที่ AI สรุปให้
/// (AI เห็น `StoryBeat.DmNotes`/`Secrets` ผ่าน `/advisor`) · ชั้น DB ก็ปิดซ้ำด้วย policy `dm_only`
/// ในย migration `AddCampaignJournalEntries` (defense in depth ไม่ใช่พึ่งเช็คใน C# ชั้นเดียว)
/// </summary>
public static class CampaignJournalEndpoints
{
    private const int MaxTitleLength = 200;  // ต้องตรงกับ HasMaxLength(200) ใน DnDEzDbContext
    private const int MaxContentLength = 8000;

    public static void MapCampaignJournalEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/journal", async (
            Guid campaignId,
            JournalEntryRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.BadRequest();
            }

            var title = request.Title.Trim();
            var content = request.Content.Trim();
            if (title.Length > MaxTitleLength || content.Length > MaxContentLength)
            {
                return Results.BadRequest();
            }

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            // ⚠️ IDOR: `sessionId` มาจาก client ตรง ๆ — ถ้าไม่เช็คว่าอยู่แคมเปญนี้จริง DM จะผูกบันทึก
            // ข้ามโต๊ะได้ (และ FK ของ Postgres จะยอมด้วยเพราะ session นั้นมีอยู่จริง แค่คนละแคมเปญ)
            if (request.SessionId is not null)
            {
                var sessionInCampaign = await db.Sessions
                    .AnyAsync(s => s.Id == request.SessionId.Value && s.CampaignId == campaignId);
                if (!sessionInCampaign)
                {
                    return Results.BadRequest(new JournalErrorResponse("session_not_in_campaign"));
                }
            }

            var entry = new CampaignJournalEntry
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                SessionId = request.SessionId,
                Title = title,
                Content = content,
                // endpoint นี้คือ "DM พิมพ์เอง" เสมอ — รายการที่ AI สรุปให้จะถูกเขียนจากเส้นทางอื่น
                // (ยังไม่มีในรอบนี้) ไม่ใช่ให้ client ส่ง authoredBy มาเองได้ ไม่งั้นปลอมที่มาได้
                AuthoredBy = JournalAuthor.Dm,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CampaignJournalEntries.Add(entry);
            await db.SaveChangesAsync();

            return Results.Ok(new JournalEntryItem(
                entry.Id, entry.SessionId, entry.Title, entry.Content, entry.AuthoredBy.ToString(), entry.CreatedAt));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/journal", async (
            Guid campaignId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            // ใหม่สุดขึ้นก่อน — DM เปิดมาเพื่อดู "เกิดอะไรขึ้นล่าสุด" ไม่ใช่ไล่อ่านจากจุดเริ่มแคมเปญ
            var entries = await db.CampaignJournalEntries.AsNoTracking()
                .Where(e => e.CampaignId == campaignId)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => new JournalEntryItem(
                    e.Id, e.SessionId, e.Title, e.Content, e.AuthoredBy.ToString(), e.CreatedAt))
                .ToListAsync();

            return Results.Ok(entries);
        }).RequireAuthorization();
    }
}

internal record JournalEntryRequest(string? Title, string? Content, Guid? SessionId);

internal record JournalErrorResponse(string Error);

internal record JournalEntryItem(
    Guid Id, Guid? SessionId, string Title, string Content, string AuthoredBy, DateTimeOffset CreatedAt);

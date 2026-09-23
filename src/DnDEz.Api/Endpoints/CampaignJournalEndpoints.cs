using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
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
/// (AI เห็น `StoryBeat.DmNotes`/`Secrets` ผ่านผู้ช่วย DM) · ชั้น DB ก็ปิดซ้ำด้วย policy `dm_only`
/// ในย migration `AddCampaignJournalEntries` (defense in depth ไม่ใช่พึ่งเช็คใน C# ชั้นเดียว)
/// </summary>
public static class CampaignJournalEndpoints
{
    private const int MaxTitleLength = 200;  // ต้องตรงกับ HasMaxLength(200) ใน DnDEzDbContext
    private const int MaxContentLength = 8000;

    private const string AiSummaryInstruction =
        "You write a campaign journal entry for a Dungeons & Dragons Dungeon Master, summarising one play " +
        "session from its log. This journal is private to the DM and is searched later by an AI assistant " +
        "when the DM asks about past events, so be concrete and searchable: name the NPCs, places, items and " +
        "factions involved. The log includes the DM's private consultations; you may include those plans. " +
        "Write in Thai, keep proper nouns in English, use short paragraphs or bullet points, and stay under " +
        "5000 characters. Use only what the log shows; never invent events.";

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
                // endpoint นี้คือ "DM พิมพ์เอง" เสมอ — รายการที่ AI สรุปให้มาจาก `/journal/ai-summary`
                // ด้านล่าง ไม่ใช่ให้ client ส่ง authoredBy มาเองได้ ไม่งั้นปลอมที่มาได้
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

        // AI สรุปเซสชันหนึ่งเป็นบันทึก (AuthoredBy=Ai) — ทางเลือก "ให้ AI จัดการบันทึก" ที่ตกลงไว้ใน Part D
        // ใช้ log ทั้งหมดรวมแถวลับของ DM ได้ เพราะบันทึกแคมเปญเป็น DM-only ที่ชั้น RLS (`dm_only`) และ tool
        // `search_campaign_journal` เสนอเฉพาะเทิร์นของ DM
        app.MapPost("/campaigns/{campaignId:guid}/journal/ai-summary", async (
            Guid campaignId,
            JournalAiSummaryRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient) =>
        {
            if (request.SessionId is null)
            {
                return Results.BadRequest();
            }

            var customTitle = request.Title?.Trim();
            if (customTitle is { Length: > MaxTitleLength })
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

            // ⚠️ IDOR: sessionId มาจาก client — ต้องอยู่ในแคมเปญนี้จริง (เหมือน POST ด้านบน)
            var session = await db.Sessions.AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == request.SessionId.Value && s.CampaignId == campaignId);
            if (session is null)
            {
                return Results.BadRequest(new JournalErrorResponse("session_not_in_campaign"));
            }

            var rawApiKey = await DmApiKeyResolver.ResolveAsync(db, dataProtectionProvider, callerUserId.Value);
            if (rawApiKey is null)
            {
                return Results.BadRequest(new JournalErrorResponse("dm_api_key_not_set"));
            }

            var transcript = await SessionTranscript.BuildAsync(db, session.Id, includeDmPrivate: true);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return Results.BadRequest(new JournalErrorResponse("session_has_no_messages"));
            }

            var turn = await anthropicClient.SendTurnAsync(
                rawApiKey, AiSummaryInstruction,
                [new AnthropicHistoryMessage("user", "Session log (oldest first):\n" + transcript)],
                [], CancellationToken.None);

            var content = turn.Text.Trim();
            if (content.Length > MaxContentLength)
            {
                content = content[..MaxContentLength];
            }

            var sessionDate = (session.StartedAt ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-dd");
            var entry = new CampaignJournalEntry
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                SessionId = session.Id,
                Title = string.IsNullOrEmpty(customTitle) ? $"สรุปเซสชัน {sessionDate}" : customTitle,
                Content = content,
                AuthoredBy = JournalAuthor.Ai,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CampaignJournalEntries.Add(entry);
            await db.SaveChangesAsync();

            return Results.Ok(new JournalEntryItem(
                entry.Id, entry.SessionId, entry.Title, entry.Content, entry.AuthoredBy.ToString(), entry.CreatedAt));
        }).RequireAuthorization();

        // แก้ได้ทุกรายการรวมที่ AI/ระบบเขียน — AI สรุปผิดต้องแก้ได้ · `AuthoredBy` คงเดิม (บอกที่มา ไม่ใช่
        // คนแก้ล่าสุด) · `SessionId` แก้ที่นี่ไม่ได้ ตัดปัญหา IDOR ผูกข้ามแคมเปญไปทั้งเส้น
        app.MapPut("/campaigns/{campaignId:guid}/journal/{entryId:guid}", async (
            Guid campaignId,
            Guid entryId,
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

            // ⚠️ IDOR: entryId มาจาก client — ต้องอยู่ในแคมเปญที่ผ่านเช็ค DM มาแล้วเท่านั้น
            var entry = await db.CampaignJournalEntries
                .SingleOrDefaultAsync(e => e.Id == entryId && e.CampaignId == campaignId);
            if (entry is null)
            {
                return Results.NotFound();
            }

            entry.Title = title;
            entry.Content = content;
            await db.SaveChangesAsync();

            return Results.Ok(new JournalEntryItem(
                entry.Id, entry.SessionId, entry.Title, entry.Content, entry.AuthoredBy.ToString(), entry.CreatedAt));
        }).RequireAuthorization();

        app.MapDelete("/campaigns/{campaignId:guid}/journal/{entryId:guid}", async (
            Guid campaignId,
            Guid entryId,
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

            var deleted = await db.CampaignJournalEntries
                .Where(e => e.Id == entryId && e.CampaignId == campaignId)
                .ExecuteDeleteAsync();

            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization();
    }
}

internal record JournalEntryRequest(string? Title, string? Content, Guid? SessionId);

internal record JournalErrorResponse(string Error);

internal record JournalAiSummaryRequest(Guid? SessionId, string? Title);

internal record JournalEntryItem(
    Guid Id, Guid? SessionId, string Title, string Content, string AuthoredBy, DateTimeOffset CreatedAt);

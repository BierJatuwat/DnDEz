using System.Security.Claims;
using System.Text;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// สรุปเซสชัน (`recap_snapshots`) — ชั้นความจำแบบบีบอัดของแผน "DM AI Assistant" ที่ผู้ช่วย DM
/// (`AssistantPrompt`) อ่านเข้า system prompt มาตั้งแต่ Part C แต่ไม่เคยมีอะไรเขียนลงเลยจนถึงไฟล์นี้
///
/// ## สองฉบับในแถวเดียว และทำไมสร้างแยกกันคนละ call
/// - `DmVersionText` — สำหรับ DM และสำหรับ AI ใช้เป็นความจำ: สร้างจาก log **ทั้งหมด** รวมที่ DM ปรึกษา
///   ลับ ๆ (`Message.DmPrivate`) + บันทึกแคมเปญของเซสชันนั้น
/// - `PlayerVersionText` — "ตอนที่แล้ว…" ให้ผู้เล่นอ่าน: สร้างจาก log **ที่ผู้เล่นเห็นอยู่แล้วเท่านั้น**
///
/// ⚠️ ห้ามรวมเป็น call เดียวแบบ "ส่งทุกอย่างไปแล้วสั่งให้ Claude แยกเองว่าอะไรลับ" — คำสั่งใน prompt ไม่ใช่
/// ขอบเขตความปลอดภัย โมเดลพลาดได้เสมอ · ฉบับผู้เล่นจึงต้องมาจาก input ที่ไม่มีความลับอยู่ตั้งแต่ต้น
///
/// ## ผู้เล่นเห็นอะไร
/// RLS ให้สมาชิกอ่านแถวได้ (`member_read`) แต่แยกระดับคอลัมน์ไม่ได้ — `DmVersionText` จึงกันที่
/// <see cref="Project"/> ในไฟล์นี้ (เขียนได้เฉพาะ DM ที่ชั้น RLS: `dm_write`)
///
/// เก็บหนึ่งแถวต่อเซสชัน: สร้างใหม่ = แทนที่ของเดิม (ไม่เก็บประวัติเวอร์ชัน ไม่มีใครต้องการมัน)
/// </summary>
public static class SessionRecapEndpoints
{
    private const int MaxRecapLength = 8000;

    private const string PlayerRecapInstruction =
        "You write the \"previously on...\" recap that the PLAYERS of a Dungeons & Dragons campaign read " +
        "at the start of their next session. Use ONLY events that appear in the session log you are given — " +
        "never invent events, and never guess at anything the log does not show. Write in Thai, in 3 to 6 " +
        "vivid sentences of flowing prose, second person plural (\"พวกคุณ\"). Do not state enemies' exact " +
        "hit points or armour class. Keep proper nouns (monster, spell, item names) in English.";

    private const string DmRecapInstruction =
        "You write a private recap of a Dungeons & Dragons session for the Dungeon Master's own notes. " +
        "It will also be given back to an AI assistant later as memory of what happened, so be factual and " +
        "specific. The log includes the DM's private consultations — you may use and mention those plans and " +
        "secrets freely here. Write in Thai as short bullet points covering: what happened, key decisions, " +
        "NPCs and places involved, loot or resources gained or spent, and unresolved threads or hooks for the " +
        "next session. Use only what the log and notes show; do not invent. Keep proper nouns in English.";

    public static void MapSessionRecapEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/recap/generate", async (
            Guid campaignId,
            Guid sessionId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient) =>
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

            if (!await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId))
            {
                return Results.NotFound();
            }

            var rawApiKey = await DmApiKeyResolver.ResolveAsync(db, dataProtectionProvider, callerUserId.Value);
            if (rawApiKey is null)
            {
                return Results.BadRequest(new RecapErrorResponse("dm_api_key_not_set"));
            }

            var result = await GenerateAsync(db, anthropicClient, rawApiKey, campaignId, sessionId);
            return result.IsOk
                ? Results.Ok(Project(result.Value!, isDm: true))
                : Results.BadRequest(new RecapErrorResponse(result.Error!));
        }).RequireAuthorization();

        // DM เขียน/แก้เองได้ทั้งสองฉบับ — AI สรุปผิดต้องแก้ได้ และ DM ที่ไม่ได้ตั้ง API key ก็ต้องมี recap ได้
        app.MapPut("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/recap", async (
            Guid campaignId,
            Guid sessionId,
            RecapUpdateRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            if (request.DmVersionText is null || request.PlayerVersionText is null)
            {
                return Results.BadRequest();
            }

            var dmText = request.DmVersionText.Trim();
            var playerText = request.PlayerVersionText.Trim();
            if (dmText.Length > MaxRecapLength || playerText.Length > MaxRecapLength)
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

            if (!await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId))
            {
                return Results.NotFound();
            }

            var recap = await db.RecapSnapshots
                .Where(r => r.SessionId == sessionId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();
            if (recap is null)
            {
                recap = new RecapSnapshot { Id = Guid.NewGuid(), SessionId = sessionId };
                db.RecapSnapshots.Add(recap);
            }

            recap.DmVersionText = dmText;
            recap.PlayerVersionText = playerText;
            recap.CreatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(Project(recap, isDm: true));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/recap", async (
            Guid campaignId,
            Guid sessionId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            // RLS ของ `campaigns` คืนเฉพาะแคมเปญที่ผู้เรียกเป็นสมาชิก — คนนอกได้ 404 (ไม่ใช่ 403) ตาม
            // precedent เดิมของ SceneEndpoints
            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            if (!await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId))
            {
                return Results.NotFound();
            }

            var recap = await db.RecapSnapshots.AsNoTracking()
                .Where(r => r.SessionId == sessionId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            return recap is null
                ? Results.NoContent()
                : Results.Ok(Project(recap, isDm: campaign.OwnerUserId == callerUserId.Value));
        }).RequireAuthorization();

        // "ตอนที่แล้ว…" — recap ล่าสุดของทั้งแคมเปญ ให้หน้าผู้เล่นโชว์ได้โดยไม่ต้องรู้ว่าเซสชันไหนคือตอนก่อน
        app.MapGet("/campaigns/{campaignId:guid}/recaps/latest", async (
            Guid campaignId,
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

            var isDm = campaign.OwnerUserId == callerUserId.Value;
            var recap = await db.RecapSnapshots.AsNoTracking()
                .Where(r => db.Sessions.Any(s => s.Id == r.SessionId && s.CampaignId == campaignId))
                // ผู้เล่นไม่ควรได้ "recap ว่าง" (เซสชันที่มีแต่การปรึกษาลับ) มาแทนอันล่าสุดที่มีเนื้อหาจริง
                .Where(r => isDm || r.PlayerVersionText != "")
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            return recap is null ? Results.NoContent() : Results.Ok(Project(recap, isDm));
        }).RequireAuthorization();
    }

    /// <summary>
    /// สร้าง recap สองฉบับด้วย AI (แทนที่ของเดิมของเซสชันนั้น) — ใช้ร่วมกันระหว่างปุ่มบนหน้าจอกับ tool
    /// `generate_session_recap` ของผู้ช่วย DM · ผู้เรียกต้องเช็คสิทธิ์ DM + ว่าเซสชันอยู่ในแคมเปญนี้มาก่อน
    ///
    /// ⚠️ ฉบับผู้เล่นมาจาก call แยกที่ได้ input ไม่มีความลับตั้งแต่ต้น (ดู doc comment ของคลาส) — ผู้ช่วย DM
    /// ที่รู้ความลับจึงเรียกตัวนี้ได้โดยไม่รั่ว เพราะผู้ช่วยไม่ได้เป็นคนเขียนฉบับผู้เล่นเอง
    /// </summary>
    internal static async Task<OpResult<RecapSnapshot>> GenerateAsync(
        DnDEzDbContext db, IAnthropicClient anthropicClient, string rawApiKey, Guid campaignId, Guid sessionId)
    {
        var fullTranscript = await SessionTranscript.BuildAsync(db, sessionId, includeDmPrivate: true);
        if (string.IsNullOrWhiteSpace(fullTranscript))
        {
            return OpResult<RecapSnapshot>.Bad("session_has_no_messages");
        }

        var dmInput = new StringBuilder();
        dmInput.AppendLine("Session log (oldest first):");
        dmInput.AppendLine(fullTranscript);
        var journal = await db.CampaignJournalEntries.AsNoTracking()
            .Where(e => e.CampaignId == campaignId && e.SessionId == sessionId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Title, e.Content })
            .ToListAsync();
        if (journal.Count > 0)
        {
            dmInput.AppendLine("Campaign journal entries attached to this session:");
            foreach (var entry in journal)
            {
                dmInput.AppendLine($"- {entry.Title}: {entry.Content}");
            }
        }

        var dmTurn = await anthropicClient.SendTurnAsync(
            rawApiKey, DmRecapInstruction,
            [new AnthropicHistoryMessage("user", dmInput.ToString())], [], CancellationToken.None);

        // ฉบับผู้เล่นสร้างจาก transcript ที่ไม่มีแถวลับเลยตั้งแต่ต้น — ไม่ใช่ตัดออกจากฉบับ DM
        // (ดู doc comment ของคลาส) · ถ้าเซสชันนี้มีแต่การปรึกษาลับ ไม่มีอะไรที่ผู้เล่นเห็นเลย ก็ไม่มี
        // อะไรให้สรุป ปล่อยว่างไว้ให้ DM เขียนเองได้ ดีกว่าให้ AI แต่งจากความว่างเปล่า
        var publicTranscript = await SessionTranscript.BuildAsync(db, sessionId, includeDmPrivate: false);
        var playerText = string.Empty;
        if (!string.IsNullOrWhiteSpace(publicTranscript))
        {
            var playerTurn = await anthropicClient.SendTurnAsync(
                rawApiKey, PlayerRecapInstruction,
                [new AnthropicHistoryMessage("user", "Session log (oldest first):\n" + publicTranscript)],
                [], CancellationToken.None);
            playerText = playerTurn.Text;
        }

        var existing = await db.RecapSnapshots.Where(r => r.SessionId == sessionId).ToListAsync();
        db.RecapSnapshots.RemoveRange(existing);

        var recap = new RecapSnapshot
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            DmVersionText = Truncate(dmTurn.Text),
            PlayerVersionText = Truncate(playerText),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RecapSnapshots.Add(recap);
        await db.SaveChangesAsync();

        return OpResult<RecapSnapshot>.Ok(recap);
    }

    /// <summary>จุดเดียวที่ตัดสินว่าผู้เรียกเห็น `DmVersionText` ไหม — RLS กันระดับคอลัมน์ไม่ได้</summary>
    private static RecapResponse Project(RecapSnapshot recap, bool isDm) =>
        new(recap.SessionId, isDm ? recap.DmVersionText : null, recap.PlayerVersionText, recap.CreatedAt);

    private static string Truncate(string text) =>
        text.Length > MaxRecapLength ? text[..MaxRecapLength] : text;
}

internal record RecapUpdateRequest(string? DmVersionText, string? PlayerVersionText);

internal record RecapErrorResponse(string Error);

/// <summary><c>DmVersionText</c> เป็น null เสมอสำหรับผู้ที่ไม่ใช่ DM (ดู <c>SessionRecapEndpoints.Project</c>)</summary>
internal record RecapResponse(Guid SessionId, string? DmVersionText, string PlayerVersionText, DateTimeOffset CreatedAt);

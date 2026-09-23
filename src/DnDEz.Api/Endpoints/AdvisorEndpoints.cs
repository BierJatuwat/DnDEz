using System.Security.Claims;
using System.Text;
using DnDEz.Api.Assistant;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Part C ของแผน "DM AI Assistant — session-context grounding" (2026-09-22) — แชท**ที่ปรึกษาส่วนตัว
/// ของ DM** สำหรับเตรียมเซสชัน/สรุปหลังเล่น/วางแผนตอนถัดไป
///
/// ## ต่างจาก `/consult` ยังไง (สำคัญ อย่าสับสนสองอันนี้)
/// | | `/consult` (Sprint D) | `/advisor` (ไฟล์นี้) |
/// |---|---|---|
/// | ผูกกับ | **session** หนึ่ง ๆ | **แคมเปญ** (ใช้ได้ก่อนเปิด session ด้วยซ้ำ) |
/// | เก็บที่ | `messages` (ปนกับ log การเล่นจริง) | `consult_messages` (แยกขาด ไม่ปนกับ log) |
/// | tool | มี `roll_dice`/`lookup_*` | **ไม่มีเลยสักตัว** — คุยอย่างเดียว ไม่แตะ state |
/// | DmNotes/Secrets ใน prompt | **ห้ามเด็ดขาด** | **ใส่ได้** (ดูเหตุผลด้านล่าง) |
///
/// ## ทำไมที่นี่ใส่ `StoryBeat.DmNotes`/`Secrets` เข้า prompt ได้ แต่ `/consult` ห้าม
/// `OrchestratorTurnRunner` ที่ `/consult` ใช้เป็น pipeline **เดียวกับ `/actions` ของผู้เล่น** ซึ่ง
/// broadcast narration เข้ากลุ่มแคมเปญจริง — ความลับที่หลุดเข้า prompt ที่นั่นมีเส้นทางไปโผล่หน้าจอ
/// ผู้เล่นได้ · แต่ `/advisor`:
///  • เป็น DM-only ทุก endpoint (`RequireCampaignDmAsync`)
///  • **ไม่มี broadcast ใด ๆ เลย** (ไม่แตะ `IHubContext` ทั้งไฟล์)
///  • เก็บบทสนทนาไว้คนละตารางกับ log การเล่น จึงไม่มีทางไหลเข้า history ของเทิร์นที่ผู้เล่นเห็น
///  • ตาราง `consult_messages` ถูกปิดเป็น DM-only ที่ชั้น RLS ด้วย (migration
///    `AddConsultMessageDmOnlyRls`) — defense in depth ไม่ใช่พึ่งเช็คใน C# ชั้นเดียว
/// ทั้งสี่ข้อต้องจริงพร้อมกัน ถ้าวันหนึ่งมีใครเพิ่ม broadcast หรือเปิดให้ player อ่านตารางนี้
/// **ต้องเอา DmNotes/Secrets ออกจาก prompt ทันที** ไม่งั้นกลายเป็นช่องรั่วทันที
/// </summary>
public static class AdvisorEndpoints
{
    private const int MaxContentLength = 4000;

    /// <summary>
    /// จำนวนข้อความย้อนหลังสูงสุดที่ส่งเข้า Claude ต่อเทิร์น — BYOK แปลว่า DM จ่ายค่า token เอง
    /// ประวัติที่ยาวขึ้นเรื่อย ๆ ไม่มีเพดานจะกลายเป็นค่าใช้จ่ายที่โตตามอายุแคมเปญโดยที่ DM ไม่รู้ตัว
    /// (PROJECT-CONTEXT §3 "แบ่งโมเดลตามงาน (สำคัญเพราะ BYOK — ผู้ใช้จ่ายเอง)")
    ///
    /// 40 = ราว 20 คำถาม-คำตอบ ซึ่งพอสำหรับบทสนทนาเตรียมเซสชันหนึ่งรอบ · **ยังไม่ใช่กลยุทธ์ความจำ
    /// ระยะยาวจริง** — การบีบอัดด้วย `RecapSnapshot` + บันทึกแคมเปญที่ค้นได้อยู่ใน Part D
    /// </summary>
    private const int MaxHistoryMessages = 40;

    public static void MapAdvisorEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/advisor", async (
            Guid campaignId,
            AdvisorRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient) =>
        {
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

            var (campaign, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var rawApiKey = await DmApiKeyResolver.ResolveAsync(db, dataProtectionProvider, callerUserId.Value);
            if (rawApiKey is null)
            {
                return Results.BadRequest(new AdvisorErrorResponse("dm_api_key_not_set"));
            }

            var (systemPrompt, activeBeatId) = await BuildAdvisorSystemPromptAsync(db, campaign!);
            var history = await BuildHistoryAsync(db, campaignId, callerUserId.Value);
            history.Add(new AnthropicHistoryMessage("user", content));

            var dmMessage = new ConsultMessage
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                DmUserId = callerUserId.Value,
                Role = ConsultRole.Dm,
                Content = content,
                LinkedBeatId = activeBeatId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ConsultMessages.Add(dmMessage);
            await db.SaveChangesAsync();

            // tools ว่างเปล่าโดยตั้งใจ — โหมดที่ปรึกษาเป็น text-only ตามที่ตกลงไว้: คุยได้ แต่ห้าม
            // ทอยเต๋า/แก้ state ใด ๆ (การทอยจริงเกิดได้เฉพาะตอนเล่นจริงผ่าน `/consult`/`/actions`)
            var turn = await anthropicClient.SendTurnAsync(
                rawApiKey, systemPrompt, history, [], CancellationToken.None);

            var aiMessage = new ConsultMessage
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                DmUserId = callerUserId.Value,
                Role = ConsultRole.Ai,
                Content = turn.Text,
                LinkedBeatId = activeBeatId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ConsultMessages.Add(aiMessage);
            await db.SaveChangesAsync();

            return Results.Ok(new AdvisorResponse(aiMessage.Id, aiMessage.Content, activeBeatId));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/advisor", async (
            Guid campaignId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            // DM-only เหมือน POST — ผู้เล่นห้ามเห็นแม้แต่ list ว่างที่แยกแยะได้ว่า "ไม่มีแชท" กับ
            // "มีแต่ไม่ให้ดู" (แนวเดียวกับคิว proposal ใน ConsultEndpoints)
            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var messages = await db.ConsultMessages.AsNoTracking()
                .Where(m => m.CampaignId == campaignId && m.DmUserId == callerUserId.Value)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new AdvisorMessageItem(m.Id, m.Role.ToString(), m.Content, m.LinkedBeatId, m.CreatedAt))
                .ToListAsync();

            return Results.Ok(messages);
        }).RequireAuthorization();
    }

    /// <summary>
    /// system prompt ของโหมดที่ปรึกษา — **ต่างจาก `OrchestratorTurnRunner.BuildSystemPromptAsync`
    /// ตรงที่ใส่ `DmNotes`/`Secrets` เข้าไปด้วย** (ดูเหตุผลเต็มใน doc comment ของคลาส ว่าทำไมที่นี่
    /// ปลอดภัยแต่ที่นั่นไม่) · ยังคงกรอง `ReviewStatus.Approved` เหมือนกันตาม PROJECT-CONTEXT §2
    ///
    /// คืน `ActiveBeatId` ออกมาด้วยเพื่อผูกบทสนทนาเข้ากับ beat ที่กำลังคุยถึง (`ConsultMessage.LinkedBeatId`
    /// มีไว้เพื่อการนี้มาตั้งแต่ `InitialSchema` แต่ไม่เคยถูกเขียนจริงจนถึง Part C นี้)
    /// </summary>
    private static async Task<(string Prompt, Guid? ActiveBeatId)> BuildAdvisorSystemPromptAsync(
        DnDEzDbContext db, Campaign campaign)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "You are a Dungeons & Dragons 5th edition expert advising a Dungeon Master in private, " +
            "grounded in the D&D SRD 5.2.1 (2024) revision. This is the DM's own planning channel: " +
            "no player can see it, so you may freely discuss secrets, twists, and prep notes. Focus on " +
            "rules and mechanics accuracy (encounter balance, CR/DC maths, action economy) — when you " +
            "are not certain a rule is exactly right, say so instead of guessing confidently. Respond " +
            "in the same language the DM writes in (this DM mostly writes Thai).");

        // บริบทแคมเปญ (รวมความลับ) ย้ายไปอยู่ที่ AssistantPrompt ตัวเดียว (2026-09-23) — ผู้ช่วยช่องเดียวใช้
        // ตัวเดียวกัน · endpoint นี้จะถูกลบเมื่อผู้ช่วยช่องเดียวแทนที่ครบ
        var activeBeatId = await AssistantPrompt.AppendCampaignContextAsync(sb, db, campaign);
        return (sb.ToString(), activeBeatId);
    }

    /// <summary>
    /// ประวัติบทสนทนาของ DM คนนี้ในแคมเปญนี้ — เอา <see cref="MaxHistoryMessages"/> ข้อความล่าสุด
    /// แล้วเรียงกลับเป็นลำดับเวลา (ไม่ใช่เอาอันเก่าสุด ซึ่งจะทำให้บริบทค้างอยู่ที่จุดเริ่มต้นตลอด
    /// แล้วลืมสิ่งที่เพิ่งคุยกันไป)
    /// </summary>
    private static async Task<List<AnthropicHistoryMessage>> BuildHistoryAsync(
        DnDEzDbContext db, Guid campaignId, Guid dmUserId)
    {
        var recent = await db.ConsultMessages.AsNoTracking()
            .Where(m => m.CampaignId == campaignId && m.DmUserId == dmUserId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxHistoryMessages)
            .Select(m => new { m.Role, m.Content, m.CreatedAt })
            .ToListAsync();

        return recent
            .OrderBy(m => m.CreatedAt)
            .Select(m => new AnthropicHistoryMessage(m.Role == ConsultRole.Ai ? "assistant" : "user", m.Content))
            .ToList();
    }
}

internal record AdvisorRequest(string? Content);

internal record AdvisorErrorResponse(string Error);

/// <summary>ไม่มี tool call ในนี้เลยโดยตั้งใจ — โหมดที่ปรึกษาเป็น text-only (ดู doc comment ของคลาส)</summary>
internal record AdvisorResponse(Guid MessageId, string Content, Guid? LinkedBeatId);

internal record AdvisorMessageItem(Guid Id, string Role, string Content, Guid? LinkedBeatId, DateTimeOffset CreatedAt);

using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// คิว proposal ของเทิร์นผู้เล่น (Sprint D Part 3) — approval gate: narration → tool_use (roll_dice) →
/// (ตัดสินจาก `AutonomySetting` ว่า auto-execute ทันทีหรือรอ DM approve ก่อน) → tool_result → narration
///
/// **2026-09-23** — เดิมไฟล์นี้ (`ConsultEndpoints`) มี `POST .../consult` ให้ DM คุยกับ AI ด้วย ตอนนี้
/// ถูกแทนที่ทั้งหมดด้วยผู้ช่วย DM ช่องเดียว (`AssistantEndpoints`, ดู `AssistantRunner`) ที่ทำได้ทั้งถาม
/// ข้อมูลและสั่งงานหลายขั้น ส่วนที่เหลืออยู่ในไฟล์นี้คือ**คิว proposal ของเทิร์นผู้เล่น** (`/actions`) ล้วน
/// ๆ — เมื่อ AI เจอ action ของผู้เล่นที่อยาก roll_dice แต่ dial เป็น Suggest จะสร้างเป็น proposal รอ DM
/// อนุมัติที่นี่ (ผู้ช่วย DM ใช้กลไกอนุมัติของตัวเอง คนละกลไกกับตารางนี้ ดู `ToolGate` ใน `AssistantTools`)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/ProposalEndpointsTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// ขอบเขต:
///  • **จำกัดผู้เรียกไว้แค่ DM (campaign owner) เท่านั้น** — เหตุผลไม่ใช่แค่ product design แต่เป็น
///    เหตุผลทาง RLS ล้วน ๆ: RLS ของ `api_keys` (`own_rows_only`) กรองด้วย `app.user_id` ของ**ผู้เรียก**
///    เท่านั้น ถ้าเปิดให้ player เรียกได้ query หา "key ของ DM" จะโดน RLS กรองว่างเปล่าเสมอ
///  • **จำกัด 1 รอบ tool round-trip เท่านั้น** ต่อการ resolve หนึ่งครั้ง — ยังไม่ใช่ agentic loop
///    เต็มรูปที่ Claude เรียก tool ต่อเนื่องได้หลายครั้งในเทิร์นเดียว (ผู้ช่วย DM ทำแบบนั้นได้แล้วแยกต่างหาก)
///  • เรียก Anthropic จริงไม่ได้ในเทสต์อัตโนมัติ — เทสต์ทั้งหมดสลับ `IAnthropicClient` เป็น fake
///    (ดู tests/DnDEz.Api.Tests/Fakes/FakeAnthropicClient.cs) ไม่มีการยิง HTTP ออกไปจริงในเทสต์เลย
///
/// **Approval gate** — ผูกกับ `AutonomySetting.Subsystem = RulesCombat` ของแคมเปญ:
///  • ไม่มีแถว `AutonomySetting` เลย (ยังไม่เคยตั้งค่า) → ถือว่าเป็น `Suggest` (default เดียวกับที่
///    entity ประกาศไว้เอง)
///  • `Manual` → ไม่เสนอ tool `roll_dice` ให้ Claude เห็นเลย
///  • `Suggest` → ถ้า Claude อยากเรียก tool จะ**ไม่ execute ทันที** สร้างเป็น `Proposal` (Pending)
///    แทน + broadcast แจ้ง DM console ผ่าน SignalR (`ProposalCreated`) DM ต้องเรียก
///    `POST .../proposals/{id}/resolve` เองถึงจะ execute จริง — narration ที่ Claude พูดก่อนจะ
///    เรียก tool (ถ้ามี) เก็บไว้เป็น `Proposal.Reason` ให้ DM เห็นบริบท
///  • `Autonomous` → execute ทันทีไม่ต้องรอใคร confirm
///
/// จุดที่ห้ามพลาด:
///  • roll_dice ที่ AI เรียกต้อง persist เป็นแถว `dice_rolls` จริงเหมือนทุกการทอยในระบบนี้ (PROJECT-
///    CONTEXT §3 ข้อบังคับสถาปัตยกรรม "RNG อยู่ฝั่ง server + เก็บ seed") — ห้ามข้าม DiceEngine ไปคำนวณ
///    เองแบบ ad-hoc และห้าม seed หลุดเข้า tool_result/ToolCalls jsonb/response เด็ดขาด **รวมถึงตอน
///    Suggest ด้วย — ห้ามทอยดักไว้ล่วงหน้าตอนสร้าง Proposal เด็ดขาด (ยังไม่รู้ผลจนกว่า DM จะ approve
///    จริง ไม่งั้นผลจะรั่วให้ AI/DM เห็นก่อน approve ซึ่งขัดกับความหมายของการ "รอ approve")**
///  • expression ที่ AI ส่งมาผิดรูปแบบต้องกลายเป็น tool_result ข้อความ error ไม่ใช่โยน exception
///  • broadcast (`ProposalCreated`) wrap ด้วย try/catch log-and-swallow — broadcast fail ต้องไม่ทำให้
///    Proposal ที่ persist สำเร็จไปแล้วถูก rollback
/// </summary>
public static class ProposalEndpoints
{
    public static void MapProposalEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/proposals/{proposalId:guid}/resolve", async (
            Guid campaignId,
            Guid proposalId,
            ResolveProposalRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient) =>
        {
            if (request.Action != "approve" && request.Action != "reject")
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

            if (campaign.OwnerUserId != callerUserId.Value)
            {
                return Results.Forbid();
            }

            var proposal = await db.Proposals.SingleOrDefaultAsync(p => p.Id == proposalId);
            if (proposal is null)
            {
                return Results.NotFound();
            }

            var proposalBelongsToCampaign = await db.Sessions
                .AnyAsync(s => s.Id == proposal.SessionId && s.CampaignId == campaignId);
            if (!proposalBelongsToCampaign)
            {
                return Results.NotFound();
            }

            if (proposal.Status != ProposalStatus.Pending)
            {
                // fast-path เฉย ๆ — ตัวที่บังคับจริงคือ ExecuteUpdateAsync ด้านล่าง (ดูเหตุผลเต็ม)
                return Results.Conflict();
            }

            if (request.Action == "reject")
            {
                // พบระหว่าง retroactive review (Sprint D Part 3) — เดิม read-then-write (เช็ค
                // proposal.Status ที่ tracked entity แล้ว mutate + SaveChangesAsync) ไม่ atomic เลย
                // สอง resolve พร้อมกันอาจเห็น Pending พร้อมกันทั้งคู่แล้วทำงานซ้ำสองรอบ — เปลี่ยนเป็น
                // ExecuteUpdateAsync แบบมี WHERE Status=Pending ในตัว (single atomic UPDATE ที่ Postgres
                // lock แถวให้เอง) แค่ตัวเดียวใน request ที่แข่งกันจะได้ affected row = 1 ตัวที่เหลือได้ 0
                var claimedRows = await db.Proposals
                    .Where(p => p.Id == proposalId && p.Status == ProposalStatus.Pending)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.Status, ProposalStatus.Rejected)
                        .SetProperty(p => p.ResolvedByUserId, callerUserId.Value)
                        .SetProperty(p => p.ResolvedAt, DateTimeOffset.UtcNow));

                if (claimedRows == 0)
                {
                    return Results.Conflict();
                }

                return Results.Ok(new ProposalResolutionResponse(
                    proposal.Id, ProposalStatus.Rejected.ToString(), MessageId: null, Content: null, ToolCall: null));
            }

            var rawApiKey = await DmApiKeyResolver.ResolveAsync(db, dataProtectionProvider, callerUserId.Value);
            if (rawApiKey is null)
            {
                // ยังไม่แตะ Status เลย — คงเป็น Pending ต่อไป ให้ DM ตั้ง key แล้วมา resolve ใหม่ได้
                return Results.BadRequest(new ConsultErrorResponse("dm_api_key_not_set"));
            }

            // claim ก่อน execute อะไรเลย (ดูเหตุผลเต็มที่ reject ด้านบน) — ถ้า claim ไม่ติด (แพ้ race
            // หรือถูก reject ไปแล้ว) ต้องหยุดตรงนี้เลย ห้าม roll ลูกเต๋า/เรียก Anthropic ต่อเด็ดขาด
            var approveClaimedRows = await db.Proposals
                .Where(p => p.Id == proposalId && p.Status == ProposalStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Status, ProposalStatus.Approved)
                    .SetProperty(p => p.ResolvedByUserId, callerUserId.Value)
                    .SetProperty(p => p.ResolvedAt, DateTimeOffset.UtcNow));

            if (approveClaimedRows == 0)
            {
                return Results.Conflict();
            }

            var payload = JsonSerializer.Deserialize<PendingProposalPayload>(proposal.ToolCallJson)
                ?? throw new InvalidOperationException("Proposal.ToolCallJson parse ไม่ได้ — ข้อมูลเสียหาย");

            // ถ้าอะไรพังตั้งแต่บรรทัดนี้ลงไป (เช่น Anthropic เรียกไม่สำเร็จ) exception จะลอยขึ้นไปโดน
            // catch-all ของ RLS middleware ที่ rollback ทั้ง transaction — claim ข้างบนจะถูก undo ไป
            // ด้วยเลย ทำให้ proposal กลับไปเป็น Pending ให้ retry ใหม่ได้จริง ไม่ค้างสถานะครึ่ง ๆ กลาง ๆ
            var toolResult = OrchestratorTurnRunner.ExecuteRollDice(db, proposal.SessionId, payload.ToolInput);
            // Proposal ถูกสร้างได้เฉพาะตอน Claude เรียก roll_dice เท่านั้น (lookup tool ที่อ่านอย่าง
            // เดียวไม่ผ่าน gate นี้เลย ดู doc comment ของ OrchestratorTurnRunner ส่วน Part B) แต่ยัง
            // ต้องส่ง tools ชุดเต็มเดียวกับตอน SendTurnAsync รอบแรก (2026-09-22) ไม่ใช่แค่ roll_dice —
            // ชุดนี้คงที่ ไม่ได้ขึ้นกับ state ที่เปลี่ยนได้ (ต่างจาก system prompt ที่ต้องเก็บจาก payload
            // เพราะ roster/beat เปลี่ยนได้) จึงประกอบใหม่ตรงนี้ได้เลยอย่างปลอดภัย
            var toolsOffered = new List<AnthropicToolDefinition>
            {
                OrchestratorTurnRunner.LookupRuleTool, OrchestratorTurnRunner.LookupMonsterTool,
                OrchestratorTurnRunner.RollDiceTool,
            };
            var secondTurn = await anthropicClient.ContinueWithToolResultAsync(
                rawApiKey, payload.SystemPrompt, payload.History, toolsOffered,
                payload.RawAssistantContent, payload.ToolUseId, toolResult, CancellationToken.None);

            var toolCallResponse = new ConsultToolCallResponse(payload.ToolName, payload.ToolInput, toolResult);
            var toolCallsJson = JsonSerializer.Serialize(new[]
            {
                new { name = toolCallResponse.Name, input = toolCallResponse.Input, result = toolCallResponse.Result },
            });

            var aiMessage = OrchestratorTurnRunner.BuildAiMessage(proposal.SessionId, secondTurn.Text, toolCallsJson);
            db.Messages.Add(aiMessage);
            await db.SaveChangesAsync();

            return Results.Ok(new ProposalResolutionResponse(
                proposal.Id, ProposalStatus.Approved.ToString(), aiMessage.Id, aiMessage.Content, toolCallResponse));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/proposals", async (
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

            // DM-only เหมือน consult/resolve ในไฟล์นี้ — ผู้เล่นห้ามเห็นคิว proposal แม้แต่ empty list
            // ที่แยกแยะได้จาก "ไม่มี proposal" (locked design decision — ดู doc comment คลาส)
            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            var pendingProposals = await db.Proposals
                .Where(p => p.SessionId == sessionId && p.Status == ProposalStatus.Pending)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync();

            var list = pendingProposals.Select(p =>
            {
                var payload = JsonSerializer.Deserialize<PendingProposalPayload>(p.ToolCallJson)
                    ?? throw new InvalidOperationException("Proposal.ToolCallJson parse ไม่ได้ — ข้อมูลเสียหาย");
                return new ProposalListItem(
                    p.Id, p.SessionId, p.Subsystem.ToString(), payload.ToolName, payload.ToolInput,
                    p.Reason, p.Status.ToString(), p.CreatedAt);
            }).ToList();

            return Results.Ok(list);
        }).RequireAuthorization();
    }

}

internal record ConsultErrorResponse(string Error);

internal record ConsultToolCallResponse(string Name, JsonElement Input, string Result);

internal record ConsultPendingResponse(Guid ProposalId, string ToolName, JsonElement ToolInput);

/// <summary>ไม่มี Seed หรือ raw API key ในนี้เด็ดขาด — MessageId/ToolCall เป็น null ตอน Pending
/// (ยังไม่มี narration สุดท้ายจนกว่า DM จะ resolve)</summary>
internal record ConsultResponse(Guid? MessageId, string Content, ConsultToolCallResponse? ToolCall, ConsultPendingResponse? Pending);

internal record ResolveProposalRequest(string? Action);

internal record ProposalResolutionResponse(
    Guid ProposalId, string Status, Guid? MessageId, string? Content, ConsultToolCallResponse? ToolCall);

/// <summary>เก็บใน Proposal.ToolCallJson ระหว่างรอ DM approve — ต้องมีครบทุกอย่างที่ต้องใช้ resume
/// บทสนทนาต่อได้โดยไม่ต้องพึ่ง state อื่นที่อาจเปลี่ยนไปแล้วระหว่างรอ (เช่น messages ใหม่ที่เพิ่มมาคั่น)
/// <c>SystemPrompt</c> ต้องเป็นค่าเดียวกับตอน `SendTurnAsync` รอบแรกเป๊ะ — ห้ามประกอบใหม่ตอน resolve
/// เพราะ roster/beat อาจเปลี่ยนไปแล้วระหว่างรอ DM approve (เช่น มีคนตายกลางทาง)
///
/// **2026-09-23** — ไม่มี `OriginDmPrivate` แล้ว: proposal มาจากเทิร์นผู้เล่น (`/actions`) เท่านั้น
/// (ช่องส่วนตัวของ DM ย้ายไปผู้ช่วย DM ที่ไม่ใช้ตาราง `proposals`) คำตอบหลัง approve จึงเป็นสาธารณะเสมอ
/// ดู `OrchestratorTurnRunner.BuildAiMessage`</summary>
internal record PendingProposalPayload(
    string ToolName,
    JsonElement ToolInput,
    string ToolUseId,
    JsonElement RawAssistantContent,
    List<AnthropicHistoryMessage> History,
    string? SystemPrompt);

internal record ProposalCreatedBroadcast(
    Guid ProposalId, Guid SessionId, string Subsystem, string ToolName, JsonElement ToolInput, string? Reason);

/// <summary>รายการ item ของ GET .../proposals — shape ใกล้เคียง <see cref="ProposalCreatedBroadcast"/>
/// โดยตั้งใจ (ให้ live broadcast กับ list ตอน reload เห็นข้อมูลหน้าตาเดียวกัน) แต่เพิ่ม Status/CreatedAt
/// เข้ามาเพราะเป็น list ของหลายแถวที่ต้องเรียงลำดับ/แสดงสถานะได้</summary>
internal record ProposalListItem(
    Guid Id,
    Guid SessionId,
    string Subsystem,
    string ToolName,
    JsonElement ToolInput,
    string? Reason,
    string Status,
    DateTimeOffset CreatedAt);

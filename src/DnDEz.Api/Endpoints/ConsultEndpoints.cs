using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint D Part 2 — tool-calling loop skeleton: narration → tool_use (roll_dice) → tool_result →
/// narration จบ loop ได้จริง โดยใช้คีย์ BYOK ของ DM (Part 1) **ยังไม่มี approval gate เลย — tool call
/// auto-allow เสมอ (gate จริงเป็นของ Part 3 ที่จะผูกกับ `AutonomySetting`)**
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/ConsultEndpointsTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// ขอบเขตที่ตั้งใจแคบ (skeleton เท่านั้น — ดู PROJECT-CONTEXT §4 "Sprint D" Part 2):
///  • **จำกัดผู้เรียกไว้แค่ DM (campaign owner) เท่านั้น** — เหตุผลไม่ใช่แค่ product design
///    ("quick play ใช้ key ของ DM เท่านั้น" ตาม dm-side-design.md §7) แต่เป็นเหตุผลทาง RLS ล้วน ๆ:
///    RLS ของ `api_keys` (`own_rows_only`) กรองด้วย `app.user_id` ของ**ผู้เรียก** เท่านั้น ถ้าเปิดให้
///    player ทั่วไปเรียก endpoint นี้แล้วให้ระบบไปอ่าน "key ของ DM" (คนละ user_id) query จะโดน RLS
///    กรองจนว่างเปล่าเสมอไม่ว่า WHERE จะเขียนถูกแค่ไหน (เว้นแต่จะทำ SECURITY DEFINER function แบบ
///    `app_create_campaign()` ซึ่งเกินขอบเขต skeleton — ถ้าจะเปิดให้ player เรียกได้ในอนาคตต้องออกแบบ
///    เรื่องนี้ก่อน ไม่ใช่แค่ลบเงื่อนไข caller==owner ทิ้ง)
///  • **จำกัด 1 รอบ tool round-trip เท่านั้น** (`IAnthropicClient.SendWithSingleToolRoundTripAsync`) —
///    ยังไม่ใช่ agentic loop เต็มรูปที่ Claude เรียก tool ต่อเนื่องได้หลายครั้งในเทิร์นเดียว
///  • เรียก Anthropic จริงไม่ได้ในเทสต์อัตโนมัติ (ไม่มี key จริง/ไม่ deterministic/มีต้นทุนจริง) —
///    เทสต์ทั้งหมดสลับ `IAnthropicClient` เป็น fake ผ่าน `WebApplicationFactory` (ดู
///    tests/DnDEz.Api.Tests/Fakes/FakeAnthropicClient.cs) ไม่มีการยิง HTTP ออกไปจริงในเทสต์เลย
///
/// จุดที่ห้ามพลาด:
///  • roll_dice ที่ AI เรียกต้อง persist เป็นแถว `dice_rolls` จริงเหมือนทุกการทอยในระบบนี้ (PROJECT-
///    CONTEXT §3 ข้อบังคับสถาปัตยกรรม "RNG อยู่ฝั่ง server + เก็บ seed") — ห้ามข้าม DiceEngine ไปคำนวณ
///    เองแบบ ad-hoc และห้าม seed หลุดเข้า tool_result/ToolCalls jsonb/response เด็ดขาด
///  • expression ที่ AI ส่งมาผิดรูปแบบ (`DiceExpressionException`) ต้องกลายเป็น tool_result ข้อความ
///    error ส่งกลับให้ Claude เห็นเอง (ให้ตอบสนอง/ขอโทษ narration ต่อได้) ไม่ใช่โยน exception จน
///    request ทั้งก้อนล้ม 500 — ตรงกับพฤติกรรม tool-use จริงที่ tool อาจ fail แล้ว model ต้องรับมือเอง
/// </summary>
public static class ConsultEndpoints
{
    private const string ProtectorPurpose = "DnDEz.ApiKeys.v1"; // ต้องตรงกับ ApiKeyEndpoints เป๊ะ
    private const string AnthropicProvider = "anthropic";
    private const int MaxContentLength = 4000;

    private static readonly AnthropicToolDefinition RollDiceTool = new(
        Name: "roll_dice",
        Description: "Roll dice using standard tabletop RPG notation (e.g. \"1d20+5\", \"2d6\"). " +
                     "Returns the total and per-die breakdown. Use this whenever an action's outcome " +
                     "should be determined by chance.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                expression = new
                {
                    type = "string",
                    description = "Dice notation, e.g. \"1d20+5\" or \"2d6\"",
                },
            },
            required = new[] { "expression" },
        });

    public static void MapConsultEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/consult", async (
            Guid campaignId,
            Guid sessionId,
            ConsultRequest request,
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

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            // เฉพาะ DM (campaign owner) เท่านั้นที่เรียกได้ — ดู doc comment ของคลาสสำหรับเหตุผลเต็ม
            // (ผูกกับ RLS ของ api_keys ไม่ใช่แค่ product design)
            if (campaign.OwnerUserId != callerUserId.Value)
            {
                return Results.Forbid();
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            // RLS own_rows_only ยอมให้ query นี้ผ่านได้เพราะ caller ยืนยันแล้วว่า == campaign owner
            // (app.user_id ของ caller เท่ากับ user_id ของแถวที่ query หา)
            var apiKey = await db.ApiKeys.SingleOrDefaultAsync(
                k => k.UserId == callerUserId.Value && k.Provider == AnthropicProvider && k.Status == ApiKeyStatus.Active);
            if (apiKey is null)
            {
                return Results.BadRequest(new ConsultErrorResponse("dm_api_key_not_set"));
            }

            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            var rawApiKey = protector.Unprotect(apiKey.EncryptedKey);

            var existingMessages = await db.Messages
                .Where(m => m.SessionId == sessionId)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.AuthorType, m.Content })
                .ToListAsync();

            var history = existingMessages
                .Select(m => new AnthropicHistoryMessage(m.AuthorType == MessageAuthorType.Ai ? "assistant" : "user", m.Content))
                .ToList();
            history.Add(new AnthropicHistoryMessage("user", content));

            var incomingMessage = new Message
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                AuthorType = MessageAuthorType.Dm,
                AuthorUserId = callerUserId.Value,
                CharacterId = null,
                Content = content,
                ToolCalls = null,
                Source = ActionSource.DmOverride,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Messages.Add(incomingMessage);

            var conversationResult = await anthropicClient.SendWithSingleToolRoundTripAsync(
                rawApiKey,
                history,
                RollDiceTool,
                (input, _) => Task.FromResult(ExecuteRollDice(db, sessionId, input)),
                CancellationToken.None);

            string? toolCallsJson = null;
            ConsultToolCallResponse? toolCallResponse = null;
            if (conversationResult.ToolCall is not null)
            {
                var toolCall = conversationResult.ToolCall;
                toolCallsJson = JsonSerializer.Serialize(new[]
                {
                    new { name = toolCall.Name, input = toolCall.Input, result = toolCall.Result },
                });
                toolCallResponse = new ConsultToolCallResponse(toolCall.Name, toolCall.Input, toolCall.Result);
            }

            var aiMessage = new Message
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                AuthorType = MessageAuthorType.Ai,
                AuthorUserId = null,
                CharacterId = null,
                Content = conversationResult.FinalText,
                ToolCalls = toolCallsJson,
                Source = ActionSource.Ai,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Messages.Add(aiMessage);

            await db.SaveChangesAsync();

            return Results.Ok(new ConsultResponse(aiMessage.Id, aiMessage.Content, toolCallResponse));
        }).RequireAuthorization();
    }

    /// <summary>
    /// executor ของ tool `roll_dice` — persist เป็น DiceRoll จริงเสมอ (ไม่มีทาง skip ได้ตาม
    /// PROJECT-CONTEXT §3) คืนข้อความสรุปที่ **ไม่มี seed** ให้ Claude เอาไปใช้ต่อ expression ที่ผิด
    /// รูปแบบคืน error string กลับไปแทนการโยน exception (ดู doc comment ของคลาส)
    /// </summary>
    private static string ExecuteRollDice(DnDEzDbContext db, Guid sessionId, JsonElement input)
    {
        string? expression;
        try
        {
            expression = input.GetProperty("expression").GetString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            // KeyNotFoundException = ไม่มี field "expression" เลย, InvalidOperationException =
            // input ไม่ใช่ JSON object หรือ "expression" ไม่ใช่ string — ทั้งคู่ต้องกลายเป็น
            // tool_result ข้อความ error ธรรมดา ไม่ใช่โยนขึ้นไปจน request ทั้งก้อนล้ม 500
            // (ปกติ Anthropic validate ตาม input_schema ก่อนเรียก tool เสมออยู่แล้ว แต่ไม่ควรพึ่ง
            // สัญญานั้นอย่างเดียวสำหรับ input จากภายนอก)
            return "Error: missing or malformed required \"expression\" field.";
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return "Error: \"expression\" must be a non-empty dice notation string.";
        }

        RollResult roll;
        var roller = SeededDiceRoller.CreateWithSecureRandomSeed();
        try
        {
            var engine = new DiceEngine();
            roll = engine.RollSystem(expression, roller, roller.Seed);
        }
        catch (DiceExpressionException ex)
        {
            return $"Error: invalid dice expression \"{expression}\" ({ex.Message}).";
        }

        var diceRoll = new DiceRoll
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            CharacterId = null,
            Expression = roll.Expression,
            Rolls = JsonSerializer.Serialize(roll.Rolls),
            Total = roll.Total,
            Source = DiceMode.SystemRoll,
            Seed = roller.Seed,
            EnteredByUserId = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DiceRolls.Add(diceRoll);

        var breakdown = string.Join(", ", roll.Rolls.Select(r => r.Dropped ? $"~~{r.Value}~~" : r.Value.ToString()));
        return $"Rolled {roll.Expression} -> total {roll.Total} (dice: [{breakdown}])";
    }
}

internal record ConsultRequest(string? Content);

internal record ConsultErrorResponse(string Error);

internal record ConsultToolCallResponse(string Name, JsonElement Input, string Result);

/// <summary>ไม่มี Seed หรือ raw API key ในนี้เด็ดขาด</summary>
internal record ConsultResponse(Guid MessageId, string Content, ConsultToolCallResponse? ToolCall);

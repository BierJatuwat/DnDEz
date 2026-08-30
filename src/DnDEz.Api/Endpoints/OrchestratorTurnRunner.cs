using System.Text.Json;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint E Part 3 — extraction ของ orchestrator-turn pipeline ที่เดิมอยู่ใน <c>ConsultEndpoints</c>
/// เท่านั้น (Sprint D Part 2/3) ให้ทั้ง DM (<c>/consult</c>) และผู้เล่น (<c>/actions</c>) เรียกโค้ด
/// เดียวกันเป๊ะ ตาม PROJECT-CONTEXT §2: "Action ของผู้เล่นทุกอันไหลผ่าน rules engine และ approval
/// gate เดิม ไม่สร้างเส้นทางแก้ state คู่ขนาน"
///
/// **จงใจไม่ broadcast อะไรจาก path ของ DM (<paramref name="broadcastActionMessages"/>=false)** —
/// `/consult` เป็นช่องทางที่ DM คุยกับ AI ได้ทั้งเรื่อง narration สาธารณะ *และ* คำถามส่วนตัว/สปอยล์
/// (เช่น "วายร้ายควรมีแผนลับอะไร") ไม่มีทางแยกสองกรณีนี้จาก API shape เดียวกันได้ — ถ้า broadcast
/// เนื้อหาของ DM เข้ากลุ่มแคมเปญแบบไม่เลือก จะกลายเป็นบั๊กเดียวกับที่ security review เจอใน
/// Sprint E Part 2 (ProposalCreated เคยหลุดไปหา player) แค่คนละ endpoint เท่านั้น — ผู้เล่นจะเห็นสิ่งที่
/// DM ไม่ได้ตั้งใจให้เห็น จึงจำกัด broadcast ไว้เฉพาะ path ที่ผู้เล่นเป็นคนเริ่มเทิร์นเอง
/// (<c>PlayerActionEndpoints</c> เท่านั้นที่ส่ง true) ซึ่งเนื้อหาเป็นของผู้เล่นเองหรือ AI ตอบสนองการ
/// กระทำของผู้เล่นโดยตรง ไม่มีทางเป็นข้อมูลลับของ DM
/// </summary>
internal static class OrchestratorTurnRunner
{
    internal static readonly AnthropicToolDefinition RollDiceTool = new(
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

    internal static async Task<ConsultResponse> RunTurnAsync(
        DnDEzDbContext db,
        IAnthropicClient anthropicClient,
        IHubContext<CampaignHub> hubContext,
        ILogger logger,
        Campaign campaign,
        Guid sessionId,
        string rawApiKey,
        string content,
        MessageAuthorType authorType,
        ActionSource source,
        Guid? authorUserId,
        Guid? characterId,
        bool broadcastActionMessages)
    {
        var autonomyLevel = await GetEffectiveAutonomyLevelAsync(db, campaign.Id, AutonomySubsystem.RulesCombat);
        var toolToOffer = autonomyLevel == AutonomyLevel.Manual ? null : RollDiceTool;

        var history = await BuildHistoryAsync(db, sessionId);
        history.Add(new AnthropicHistoryMessage("user", content));

        var incomingMessage = new Message
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            AuthorType = authorType,
            AuthorUserId = authorUserId,
            CharacterId = characterId,
            Content = content,
            ToolCalls = null,
            Source = source,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Messages.Add(incomingMessage);
        await db.SaveChangesAsync();

        if (broadcastActionMessages)
        {
            await BroadcastActionMessageAsync(hubContext, logger, campaign.Id, incomingMessage);
        }

        var firstTurn = await anthropicClient.SendTurnAsync(rawApiKey, history, toolToOffer, CancellationToken.None);

        if (firstTurn.ToolUse is null)
        {
            // Claude ตอบ narration จบเลย ไม่มี tool ให้ต้องตัดสินใจ auto/gate เลย — เหมือนกันทุก level
            var narrationMessage = BuildAiMessage(sessionId, firstTurn.Text, toolCallsJson: null);
            db.Messages.Add(narrationMessage);
            await db.SaveChangesAsync();

            if (broadcastActionMessages)
            {
                await BroadcastActionMessageAsync(hubContext, logger, campaign.Id, narrationMessage);
            }

            return new ConsultResponse(narrationMessage.Id, narrationMessage.Content, ToolCall: null, Pending: null);
        }

        if (autonomyLevel == AutonomyLevel.Suggest)
        {
            var proposal = new Proposal
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Subsystem = AutonomySubsystem.RulesCombat,
                ToolCallJson = JsonSerializer.Serialize(new PendingProposalPayload(
                    firstTurn.ToolUse.Name,
                    firstTurn.ToolUse.Input,
                    firstTurn.ToolUse.Id,
                    firstTurn.RawAssistantContent,
                    history)),
                Reason = string.IsNullOrWhiteSpace(firstTurn.Text) ? null : firstTurn.Text,
                Status = ProposalStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Proposals.Add(proposal);
            await db.SaveChangesAsync();

            try
            {
                // DM-only เสมอ ไม่ว่าใครเป็นคนเริ่มเทิร์น (player action หรือ DM consult) — ผู้เล่น
                // ห้ามเห็นคิว proposal ตาม locked decision เดิม (ดู doc comment ของ ConsultEndpoints
                // และบั๊กที่ security review เจอใน Sprint E Part 2)
                await hubContext.Clients.User(campaign.OwnerUserId.ToString()).SendAsync(
                    "ProposalCreated",
                    new ProposalCreatedBroadcast(
                        proposal.Id, sessionId, proposal.Subsystem.ToString(),
                        firstTurn.ToolUse.Name, firstTurn.ToolUse.Input, proposal.Reason));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast ProposalCreated for proposal {ProposalId}", proposal.Id);
            }

            return new ConsultResponse(
                MessageId: null, Content: firstTurn.Text, ToolCall: null,
                Pending: new ConsultPendingResponse(proposal.Id, firstTurn.ToolUse.Name, firstTurn.ToolUse.Input));
        }

        // Autonomous — execute ทันที (Manual ไม่มีทางมาถึงบรรทัดนี้ได้เลยเพราะไม่เสนอ tool ให้ตั้งแต่ต้น)
        var toolResult = ExecuteRollDice(db, sessionId, firstTurn.ToolUse.Input);
        var secondTurn = await anthropicClient.ContinueWithToolResultAsync(
            rawApiKey, history, RollDiceTool, firstTurn.RawAssistantContent,
            firstTurn.ToolUse.Id, toolResult, CancellationToken.None);

        var toolCallResponse = new ConsultToolCallResponse(firstTurn.ToolUse.Name, firstTurn.ToolUse.Input, toolResult);
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { name = toolCallResponse.Name, input = toolCallResponse.Input, result = toolCallResponse.Result },
        });

        var aiMessage = BuildAiMessage(sessionId, secondTurn.Text, toolCallsJson);
        db.Messages.Add(aiMessage);
        await db.SaveChangesAsync();

        if (broadcastActionMessages)
        {
            await BroadcastActionMessageAsync(hubContext, logger, campaign.Id, aiMessage);
        }

        return new ConsultResponse(aiMessage.Id, aiMessage.Content, toolCallResponse, Pending: null);
    }

    private static async Task BroadcastActionMessageAsync(
        IHubContext<CampaignHub> hubContext, ILogger logger, Guid campaignId, Message message)
    {
        try
        {
            await hubContext.Clients.Group(CampaignHub.GroupName(campaignId)).SendAsync(
                "ActionMessageCreated",
                new ActionMessageCreatedBroadcast(
                    message.Id,
                    message.SessionId,
                    message.AuthorType.ToString(),
                    message.CharacterId,
                    message.Content,
                    message.CreatedAt));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast ActionMessageCreated for message {MessageId}", message.Id);
        }
    }

    internal static async Task<AutonomyLevel> GetEffectiveAutonomyLevelAsync(
        DnDEzDbContext db, Guid campaignId, AutonomySubsystem subsystem)
    {
        var setting = await db.AutonomySettings
            .Where(a => a.CampaignId == campaignId && a.Subsystem == subsystem)
            .Select(a => (AutonomyLevel?)a.Level)
            .SingleOrDefaultAsync();
        // ไม่มีแถว = ยังไม่เคยตั้งค่า — default เดียวกับที่ entity ประกาศไว้เอง
        return setting ?? AutonomyLevel.Suggest;
    }

    private static async Task<List<AnthropicHistoryMessage>> BuildHistoryAsync(DnDEzDbContext db, Guid sessionId)
    {
        var existingMessages = await db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.AuthorType, m.Content })
            .ToListAsync();

        return existingMessages
            .Select(m => new AnthropicHistoryMessage(m.AuthorType == MessageAuthorType.Ai ? "assistant" : "user", m.Content))
            .ToList();
    }

    internal static Message BuildAiMessage(Guid sessionId, string content, string? toolCallsJson) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        AuthorType = MessageAuthorType.Ai,
        AuthorUserId = null,
        CharacterId = null,
        Content = content,
        ToolCalls = toolCallsJson,
        Source = ActionSource.Ai,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// executor ของ tool `roll_dice` — persist เป็น DiceRoll จริงเสมอ (ไม่มีทาง skip ได้ตาม
    /// PROJECT-CONTEXT §3) คืนข้อความสรุปที่ **ไม่มี seed** ให้ Claude เอาไปใช้ต่อ expression ที่ผิด
    /// รูปแบบคืน error string กลับไปแทนการโยน exception
    /// </summary>
    internal static string ExecuteRollDice(DnDEzDbContext db, Guid sessionId, JsonElement input)
    {
        string? expression;
        try
        {
            expression = input.GetProperty("expression").GetString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
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

/// <summary>payload ที่ broadcast เข้า SignalR group ของแคมเปญ (ไม่ใช่ DM-only) ตอนมี Message ใหม่
/// จาก player-action turn — ดู doc comment ของคลาสว่าทำไม DM consult ไม่ broadcast แบบนี้</summary>
internal record ActionMessageCreatedBroadcast(
    Guid MessageId,
    Guid SessionId,
    string AuthorType,
    Guid? CharacterId,
    string Content,
    DateTimeOffset CreatedAt);

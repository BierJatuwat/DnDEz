using System.Text;
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
/// **2026-09-22 — เพิ่ม system prompt (ก่อนหน้านี้ AI ไม่เห็น context ของ session/แคมเปญเลยสักบรรทัด
/// ผู้ใช้ถามแล้วตรวจโค้ดเจอ)** — ดู `BuildSystemPromptAsync` ท้ายไฟล์ ประกอบจาก: ชื่อแคมเปญ, story beat
/// ที่ active (เฉพาะ `ReviewStatus.Approved`, ตาม PROJECT-CONTEXT §2 "record ที่ยังไม่ approved ห้าม
/// ส่งเข้า orchestrator context"), roster ตัวละครผู้เล่น (HP/AC/เลเวล) **⚠️ ไม่เคยใส่
/// `StoryBeat.DmNotes`/`Secrets` เด็ดขาดใน method นี้** เพราะ method เดียวกันนี้ใช้ทั้ง path DM
/// (`/consult`, ไม่ broadcast) และ path ผู้เล่น (`/actions`, broadcast เข้ากลุ่มแคมเปญจริง — ดู
/// `broadcastActionMessages` ด้านล่าง) ถ้าใส่ DmNotes/Secrets เข้าไปแล้ว AI พูดถึงมันตอนตอบ narration
/// ของ player-action turn เนื้อหานั้นจะหลุดไปหาผู้เล่นทันทีผ่าน broadcast — เป็นบั๊กคลาสเดียวกับที่
/// security review เจอตอน `ProposalCreated` หลุดไปหา player ใน Sprint E Part 2 (ดู doc comment ด้านล่าง)
/// เพียงแต่รั่วผ่าน AI narration แทนที่จะรั่วผ่าน broadcast payload ตรง ๆ · จะให้ AI ใช้ DmNotes/Secrets
/// ได้ต้องเป็น endpoint ใหม่ที่ไม่มีทาง broadcast เด็ดขาด (ที่ปรึกษา DM ส่วนตัว — ยังไม่ทำในรอบนี้)
///
/// **จงใจไม่ broadcast อะไรจาก path ของ DM (<paramref name="broadcastActionMessages"/>=false)** —
/// `/consult` เป็นช่องทางที่ DM คุยกับ AI ได้ทั้งเรื่อง narration สาธารณะ *และ* คำถามส่วนตัว/สปอยล์
/// (เช่น "วายร้ายควรมีแผนลับอะไร") ไม่มีทางแยกสองกรณีนี้จาก API shape เดียวกันได้ — ถ้า broadcast
/// เนื้อหาของ DM เข้ากลุ่มแคมเปญแบบไม่เลือก จะกลายเป็นบั๊กเดียวกับที่ security review เจอใน
/// Sprint E Part 2 (ProposalCreated เคยหลุดไปหา player) แค่คนละ endpoint เท่านั้น — ผู้เล่นจะเห็นสิ่งที่
/// DM ไม่ได้ตั้งใจให้เห็น จึงจำกัด broadcast ไว้เฉพาะ path ที่ผู้เล่นเป็นคนเริ่มเทิร์นเอง
/// (<c>PlayerActionEndpoints</c> เท่านั้นที่ส่ง true) ซึ่งเนื้อหาเป็นของผู้เล่นเองหรือ AI ตอบสนองการ
/// กระทำของผู้เล่นโดยตรง ไม่มีทางเป็นข้อมูลลับของ DM
///
/// **2026-09-22 (Part B) — เพิ่ม tool อ่านอย่างเดียว `lookup_rule`/`lookup_monster`** เพื่อให้ AI ตอบ
/// คำถามกฎ/สเตทมอนสเตอร์ได้แม่นจากข้อมูลจริงในระบบ (SRD ที่ import/แปลไว้ + แม่แบบมอนสเตอร์ของแคมเปญ
/// ที่ DM ปรับเอง) แทนที่จะเดาจากความจำการเทรน — เพราะเป็นการอ่านอย่างเดียว ไม่มี state ให้พัง จึง
/// **ไม่ผ่าน `AutonomySetting`/`Proposal` gate เหมือน `roll_dice`** เสนอให้ Claude เห็นเสมอไม่ว่า dial
/// จะตั้งเป็นอะไร (ดู `ReadOnlyToolNames`) — ต่างจาก `roll_dice` ที่ยังต้องผ่าน gate เดิมทุกจุดเพราะ
/// เขียน `dice_rolls` จริง
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

    /// <summary>2026-09-22 (Part B) — อ่านอย่างเดียว ไม่เขียน state เลย จึงเสนอเสมอไม่ว่า autonomy
    /// dial จะเป็นอะไร (ต่างจาก <see cref="RollDiceTool"/> ที่ Manual ตัดออกเพราะเขียน dice_rolls จริง)
    /// key เป็น slug เดียวกับที่ `/srd/corpus/{category}/{key}` ใช้ (เช่น "blinded", "fire-bolt")</summary>
    internal static readonly AnthropicToolDefinition LookupRuleTool = new(
        Name: "lookup_rule",
        Description: "Look up the exact SRD 5.2.1 text for a condition, spell, or tagged rule by its " +
                     "key (a lowercase-hyphenated slug, e.g. \"blinded\", \"fire-bolt\", \"grappled\"). " +
                     "Use this whenever you are not fully certain of a rule's exact wording or numbers " +
                     "instead of relying on memory — this campaign's app may reflect a different SRD " +
                     "revision than what you were trained on.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                key = new
                {
                    type = "string",
                    description = "Lowercase-hyphenated slug, e.g. \"blinded\" or \"fire-bolt\".",
                },
            },
            required = new[] { "key" },
        });

    /// <summary>2026-09-22 (Part B) — เหตุผลเดียวกับ <see cref="LookupRuleTool"/> ที่เสนอเสมอ ·
    /// **ค่าที่คืนต้องเป็นของแคมเปญนี้ถ้า DM ปรับ stat ไว้แล้ว ไม่ใช่ค่า SRD ดั้งเดิมเสมอ** — ดู
    /// `ExecuteLookupMonster`</summary>
    internal static readonly AnthropicToolDefinition LookupMonsterTool = new(
        Name: "lookup_monster",
        Description: "Look up a monster's stat block by its key (a lowercase-hyphenated slug, e.g. " +
                     "\"goblin-warrior\", \"adult-red-dragon\"). If this campaign's DM has adjusted the " +
                     "monster's stats, you get the adjusted version, not the original SRD numbers.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                key = new
                {
                    type = "string",
                    description = "Lowercase-hyphenated slug, e.g. \"goblin-warrior\".",
                },
            },
            required = new[] { "key" },
        });

    /// <summary>2026-09-22 (Part D) — ค้น "บันทึกแคมเปญ" (`campaign_journal_entries`) ที่ DM เขียนเอง
    /// หรือ AI สรุปไว้ · หลักการคือ **ไม่ยัดบันทึกเข้า system prompt** (โตไม่จำกัดตามอายุแคมเปญ และ DM
    /// จ่ายค่า token เองแบบ BYOK) แต่ให้ AI ค้นเอาเองเมื่อต้องการจริง — เหมือน `lookup_rule`</summary>
    internal static readonly AnthropicToolDefinition SearchCampaignJournalTool = new(
        Name: "search_campaign_journal",
        Description: "Search this campaign's journal — notes the DM wrote (or you summarised earlier) " +
                     "about what has happened in past sessions. Use this when the DM refers to " +
                     "something from an earlier session that the recap in your context does not cover, " +
                     "instead of guessing or saying you don't remember.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "Words to look for in journal titles and bodies, e.g. a character or place name.",
                },
            },
            required = new[] { "query" },
        });

    /// <summary>ชื่อ tool ที่อ่านอย่างเดียว ไม่เขียน state เลย — ไม่ต้องผ่าน AutonomySetting/Proposal
    /// gate (ดู doc comment ของคลาสส่วน Part B) ต่างจาก <see cref="RollDiceTool"/> ที่เขียน dice_rolls
    /// จริงและต้องผ่าน gate เดิม</summary>
    private static readonly HashSet<string> ReadOnlyToolNames =
        ["lookup_rule", "lookup_monster", "search_campaign_journal"];

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
        // lookup tools อ่านอย่างเดียว เสนอเสมอไม่ว่า autonomy dial จะเป็นอะไร — เฉพาะ roll_dice (เขียน
        // state จริง) ที่ตัดออกตอน Manual (ดู doc comment ของ ReadOnlyToolNames ด้านบน)
        var toolsToOffer = new List<AnthropicToolDefinition> { LookupRuleTool, LookupMonsterTool };

        // ⚠️ **`search_campaign_journal` เสนอเฉพาะเทิร์นที่ DM เป็นคนเริ่มเท่านั้น** (2026-09-22, Part D)
        // เพราะบันทึกแคมเปญมีความลับของ DM ปนได้ (รายการที่ AI สรุปให้มาจาก `/advisor` ซึ่งเห็น
        // `StoryBeat.DmNotes`/`Secrets`) ส่วนเทิร์นของผู้เล่น (`/actions`) **broadcast narration เข้ากลุ่ม
        // แคมเปญจริง** ถ้าเสนอ tool นี้ให้ด้วย Claude อาจดึงความลับมาเล่าในเนื้อ narration ที่ทุกคนเห็น —
        // เป็นบั๊กคลาสเดียวกับที่กัน DmNotes/Secrets ออกจาก system prompt ไว้แล้วใน Part A
        //
        // RLS (`dm_only` บน `campaign_journal_entries`) กันซ้ำอีกชั้นอยู่แล้ว — เทิร์นของผู้เล่นรันด้วย
        // `app.user_id` ของผู้เล่น (ดู middleware ใน Program.cs) `app_is_dm()` จึงเป็น false แล้ว query
        // คืน 0 แถว — **แต่ห้ามพึ่งชั้นนั้นชั้นเดียว** เพราะถ้าวันหนึ่ง policy ถูกผ่อน (เช่นเพิ่มคอลัมน์
        // `visibility` แล้วเปิดให้สมาชิกอ่านบางรายการ) การเสนอ tool นี้บน path ที่ broadcast จะกลาย
        // เป็นช่องรั่วทันทีโดยไม่มีใครรู้ตัว · อีกเหตุผลคือถ้าเสนอไปแล้ว tool คืน 0 แถวเสมอ Claude จะ
        // ได้ context ที่ชวนเข้าใจผิดว่า "แคมเปญนี้ไม่มีบันทึกอะไรเลย" ทั้งที่มี
        if (authorType == MessageAuthorType.Dm)
        {
            toolsToOffer.Add(SearchCampaignJournalTool);
        }
        if (autonomyLevel != AutonomyLevel.Manual)
        {
            toolsToOffer.Add(RollDiceTool);
        }

        var systemPrompt = await BuildSystemPromptAsync(db, campaign, sessionId, authorType);

        // เทิร์นที่ไม่ broadcast (`/consult`) = ช่องส่วนตัวของ DM — ข้อความทั้งสองฝั่งของเทิร์นนี้ลับ และ
        // เห็น history ลับได้ · เทิร์นที่ broadcast ห้ามเห็น history ลับเด็ดขาด ไม่ว่าคนกดจะเป็นใคร — ผูกกับ
        // การ broadcast ไม่ใช่ตัวตน เพราะ DM ที่เล่นตัวละครตัวเองผ่าน `/actions` ก็ broadcast และ RLS ปล่อย
        // ให้ DM อ่านแถวลับได้ filter ตรงนี้จึงเป็นตัวกันจริงของเคสนั้น (ดู `Message.DmPrivate`)
        var dmPrivate = !broadcastActionMessages;
        var history = await BuildHistoryAsync(db, sessionId, includeDmPrivate: dmPrivate);
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
            DmPrivate = dmPrivate,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Messages.Add(incomingMessage);
        await db.SaveChangesAsync();

        if (broadcastActionMessages)
        {
            await BroadcastActionMessageAsync(hubContext, logger, campaign.Id, incomingMessage);
        }

        var firstTurn = await anthropicClient.SendTurnAsync(
            rawApiKey, systemPrompt, history, toolsToOffer, CancellationToken.None);

        if (firstTurn.ToolUse is null)
        {
            // Claude ตอบ narration จบเลย ไม่มี tool ให้ต้องตัดสินใจ auto/gate เลย — เหมือนกันทุก level
            var narrationMessage = BuildAiMessage(sessionId, firstTurn.Text, toolCallsJson: null, dmPrivate);
            db.Messages.Add(narrationMessage);
            await db.SaveChangesAsync();

            if (broadcastActionMessages)
            {
                await BroadcastActionMessageAsync(hubContext, logger, campaign.Id, narrationMessage);
            }

            return new ConsultResponse(narrationMessage.Id, narrationMessage.Content, ToolCall: null, Pending: null);
        }

        if (ReadOnlyToolNames.Contains(firstTurn.ToolUse.Name))
        {
            // 2026-09-22 (Part B) — อ่านอย่างเดียว ไม่มี state ให้พัง จึง execute ทันทีเสมอ ไม่ผ่าน
            // AutonomySetting/Proposal gate เลย (gate มีไว้กันการ "เขียน" ที่ผิดพลาดแก้คืนยาก ไม่ใช่
            // การอ่าน) — โครงเหมือน Autonomous branch ของ roll_dice ด้านล่างทุกประการ ต่างแค่ executor
            var readOnlyResult = await ExecuteReadOnlyToolAsync(
                db, campaign.Id, firstTurn.ToolUse.Name, firstTurn.ToolUse.Input);
            var afterLookupTurn = await anthropicClient.ContinueWithToolResultAsync(
                rawApiKey, systemPrompt, history, toolsToOffer, firstTurn.RawAssistantContent,
                firstTurn.ToolUse.Id, readOnlyResult, CancellationToken.None);

            var lookupToolCallResponse = new ConsultToolCallResponse(firstTurn.ToolUse.Name, firstTurn.ToolUse.Input, readOnlyResult);
            var lookupToolCallsJson = JsonSerializer.Serialize(new[]
            {
                new { name = lookupToolCallResponse.Name, input = lookupToolCallResponse.Input, result = lookupToolCallResponse.Result },
            });

            var lookupAiMessage = BuildAiMessage(sessionId, afterLookupTurn.Text, lookupToolCallsJson, dmPrivate);
            db.Messages.Add(lookupAiMessage);
            await db.SaveChangesAsync();

            if (broadcastActionMessages)
            {
                await BroadcastActionMessageAsync(hubContext, logger, campaign.Id, lookupAiMessage);
            }

            return new ConsultResponse(lookupAiMessage.Id, lookupAiMessage.Content, lookupToolCallResponse, Pending: null);
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
                    history,
                    systemPrompt,
                    dmPrivate)),
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

        // Autonomous — execute ทันที (Manual ไม่มีทางมาถึงบรรทัดนี้ได้เลย เพราะ toolsToOffer ไม่มี
        // roll_dice ให้เสนอตั้งแต่ต้น และ read-only tool ถูกจับไปแล้วด้านบน)
        var toolResult = ExecuteRollDice(db, sessionId, firstTurn.ToolUse.Input);
        var secondTurn = await anthropicClient.ContinueWithToolResultAsync(
            rawApiKey, systemPrompt, history, toolsToOffer, firstTurn.RawAssistantContent,
            firstTurn.ToolUse.Id, toolResult, CancellationToken.None);

        var toolCallResponse = new ConsultToolCallResponse(firstTurn.ToolUse.Name, firstTurn.ToolUse.Input, toolResult);
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { name = toolCallResponse.Name, input = toolCallResponse.Input, result = toolCallResponse.Result },
        });

        var aiMessage = BuildAiMessage(sessionId, secondTurn.Text, toolCallsJson, dmPrivate);
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

    /// <summary>
    /// ประกอบ system prompt ของเทิร์นนี้ — ดู doc comment ของคลาสว่าทำไม `DmNotes`/`Secrets` ห้ามอยู่
    /// ในนี้เด็ดขาด (method นี้ใช้ร่วมกันทั้ง path DM และ path ผู้เล่นที่ broadcast จริง)
    ///
    /// ขอบเขตสะสม: ชื่อแคมเปญ + story beat ที่ active (เฉพาะ approved) + roster ตัวละครผู้เล่น
    /// (HP/AC/เลเวล) (Part A) · **2026-09-22 (Part F) เพิ่ม encounter ที่ active ของ session นี้** —
    /// ลำดับเทิร์น + ใครกำลังเดินอยู่ + HP/AC สดของผู้เข้าร่วมทุกตัว (ทั้งตัวละครผู้เล่นและมอนสเตอร์ที่
    /// spawn จาก Part E) ทำให้คำถามกลางไฟต์แบบ "บอสเหลือเลือดเท่าไหร่ ควรทำไง" มีตัวเลขจริงให้ AI คิดต่อ
    ///
    /// ⚠️ **HP/AC ของมอนสเตอร์ที่ `NpcStatsVisibleToParty=false` ถูกซ่อนจาก prompt ของเทิร์นผู้เล่น**
    /// (ใช้ `CharacterStatsVisibility.IsVisibleTo` ตัวเดียวกับที่ `GET /campaigns/{id}/characters` ใช้ —
    /// เกณฑ์ต้องตรงกันเป๊ะ ไม่ใช่สร้างเกณฑ์คู่ขนาน) เหตุผลเดียวกับที่กัน `DmNotes`/`Secrets` ไว้: เทิร์น
    /// ของผู้เล่น (`/actions`) broadcast narration จริง ถ้าเลขที่ควรซ่อนอยู่ใน prompt Claude มีโอกาส
    /// พูดตัวเลขนั้นออกมาตอนตอบ narration ซึ่งหลุดไปหาผู้เล่นทันที — ตรงข้ามกับหลักการที่ผู้เล่นต้อง
    /// ค้นหา AC/HP ศัตรูเอาเองในเกม (ดู `CharacterStatsVisibility` doc comment) เทิร์นของ DM เห็นเลขเต็ม
    /// เสมอเพราะ DM รู้ค่าที่แท้จริงอยู่แล้ว
    /// </summary>
    private static async Task<string> BuildSystemPromptAsync(
        DnDEzDbContext db, Campaign campaign, Guid sessionId, MessageAuthorType authorType)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "You are an AI assistant embedded in a Dungeons & Dragons 5th edition virtual tabletop " +
            "application, grounded in the D&D SRD 5.2.1 (2024) revision. Help the Dungeon Master and " +
            "players with narration, rules questions, and tactical suggestions. Respond in the same " +
            "language the person you're addressing writes in (this campaign's DM and players mostly " +
            "write in Thai). When you are not certain a rule or a specific creature's stats are " +
            "exactly right, say so rather than guessing confidently.");
        sb.AppendLine($"Campaign name: {campaign.Name}");

        // ReviewStatus.Approved เท่านั้น — PROJECT-CONTEXT §2 "record ที่ยังไม่ approved ห้ามส่งเข้า
        // orchestrator context" ครอบคลุมทุก entity ที่มีคอลัมน์นี้ ไม่ใช่แค่ source-import เดิม
        var activeBeat = await db.StoryBeats.AsNoTracking()
            .Where(b => b.CampaignId == campaign.Id
                && b.Progress == BeatProgress.Active
                && b.ReviewStatus == ReviewStatus.Approved)
            .Select(b => new { b.Title, b.Objective, b.Summary })
            .SingleOrDefaultAsync();
        if (activeBeat is not null)
        {
            sb.AppendLine($"Current story beat: {activeBeat.Title}");
            if (!string.IsNullOrWhiteSpace(activeBeat.Objective))
            {
                sb.AppendLine($"Objective: {activeBeat.Objective}");
            }

            if (!string.IsNullOrWhiteSpace(activeBeat.Summary))
            {
                sb.AppendLine($"Summary: {activeBeat.Summary}");
            }
        }

        // ControlledByDm == false = ตัวละครผู้เล่นจริง (ไม่ใช่ NPC/มอนสเตอร์ที่ DM คุม) — roster นี้
        // เป็น "party" ไม่ใช่ทุก Character แถวในแคมเปญ
        var roster = await db.Characters.AsNoTracking()
            .Where(c => c.CampaignId == campaign.Id && !c.ControlledByDm)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Name, c.Level, c.CurrentHp, c.MaxHp, c.CurrentTempHp, c.ArmorClass })
            .ToListAsync();
        if (roster.Count > 0)
        {
            sb.AppendLine("Party roster:");
            foreach (var c in roster)
            {
                var tempHpSuffix = c.CurrentTempHp > 0 ? $" (+{c.CurrentTempHp} temp)" : string.Empty;
                sb.AppendLine($"- {c.Name} (level {c.Level}): HP {c.CurrentHp}/{c.MaxHp}{tempHpSuffix}, AC {c.ArmorClass}");
            }
        }

        // encounter ที่ active ของ session นี้เท่านั้น (EndedAt == null) — session หนึ่งมีได้หลาย
        // encounter ตามเวลา (ดู doc comment ของ entity `Encounter`) เอาแค่ตัวที่กำลังเปิดอยู่ตอนนี้
        var activeEncounter = await db.Encounters.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.EndedAt == null)
            .Select(e => new { e.Id, e.CurrentParticipantId })
            .SingleOrDefaultAsync();

        if (activeEncounter is not null)
        {
            var isDmTurn = authorType == MessageAuthorType.Dm;
            var participants = await db.EncounterParticipants.AsNoTracking()
                .Where(p => p.EncounterId == activeEncounter.Id)
                .OrderBy(p => p.TurnOrder)
                .Join(db.Characters, p => p.CharacterId, c => c.Id, (p, c) => new
                {
                    p.Id,
                    c.Name,
                    c.CurrentHp,
                    c.MaxHp,
                    c.ArmorClass,
                    c.ControlledByDm,
                    c.NpcStatsVisibleToParty,
                })
                .ToListAsync();

            if (participants.Count > 0)
            {
                sb.AppendLine("There is an active combat encounter right now, in initiative order:");
                foreach (var p in participants)
                {
                    var turnMarker = p.Id == activeEncounter.CurrentParticipantId ? " (current turn)" : string.Empty;
                    var statsVisible = CharacterStatsVisibility.IsVisibleTo(p.ControlledByDm, p.NpcStatsVisibleToParty, isDmTurn);
                    var stats = statsVisible
                        ? $"HP {p.CurrentHp}/{p.MaxHp}, AC {p.ArmorClass}"
                        : "stats hidden from the party — never state exact numbers, describe its condition qualitatively instead (e.g. \"badly wounded\")";
                    sb.AppendLine($"- {p.Name}{turnMarker}: {stats}");
                }
            }
        }

        return sb.ToString();
    }

    private static async Task<List<AnthropicHistoryMessage>> BuildHistoryAsync(
        DnDEzDbContext db, Guid sessionId, bool includeDmPrivate)
    {
        var existingMessages = await db.Messages
            .Where(m => m.SessionId == sessionId && (includeDmPrivate || !m.DmPrivate))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.AuthorType, m.Content })
            .ToListAsync();

        return existingMessages
            .Select(m => new AnthropicHistoryMessage(m.AuthorType == MessageAuthorType.Ai ? "assistant" : "user", m.Content))
            .ToList();
    }

    internal static Message BuildAiMessage(Guid sessionId, string content, string? toolCallsJson, bool dmPrivate) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        AuthorType = MessageAuthorType.Ai,
        AuthorUserId = null,
        CharacterId = null,
        Content = content,
        ToolCalls = toolCallsJson,
        Source = ActionSource.Ai,
        DmPrivate = dmPrivate,
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

    /// <summary>2026-09-22 (Part B) — จุดเดียวที่ dispatch ไปหา executor ของ tool อ่านอย่างเดียวแต่ละตัว
    /// (มีแค่ 2 ตัวตอนนี้ แยกฟังก์ชันไว้ตั้งแต่ต้นเผื่อเพิ่มตัวที่ 3 ทีหลัง ไม่ต้องแก้จุดเรียก)</summary>
    internal static async Task<string> ExecuteReadOnlyToolAsync(
        DnDEzDbContext db, Guid campaignId, string toolName, JsonElement input) => toolName switch
    {
        "lookup_rule" => await ExecuteLookupRule(db, input),
        "lookup_monster" => await ExecuteLookupMonster(db, campaignId, input),
        "search_campaign_journal" => await ExecuteSearchCampaignJournal(db, campaignId, input),
        _ => $"Error: unknown read-only tool \"{toolName}\".", // unreachable ถ้า ReadOnlyToolNames ตรงกัน — กันไว้เฉย ๆ
    };

    /// <summary>ค้น `rules_corpus_entries` (ต้นฉบับอังกฤษ — เสถียรกว่าให้ AI อ้างอิง ไม่ต้องพึ่งว่าแปล
    /// ไทยครบหรือยัง) ด้วย key ตรงตัว — ไม่ throw เด็ดขาด (malformed input/หา key ไม่เจอ = error string
    /// กลับไปเป็น tool_result ตาม convention เดียวกับ `ExecuteRollDice`)</summary>
    private static async Task<string> ExecuteLookupRule(DnDEzDbContext db, JsonElement input)
    {
        string? key;
        try
        {
            key = input.GetProperty("key").GetString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return "Error: missing or malformed required \"key\" field.";
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return "Error: \"key\" must be a non-empty lowercase-hyphenated slug, e.g. \"blinded\" or \"fire-bolt\".";
        }

        var normalizedKey = key.Trim().ToLowerInvariant();
        var matches = await db.RulesCorpusEntries.AsNoTracking()
            .Where(e => e.Key == normalizedKey)
            .Select(e => new { e.Category, e.Title, e.Content })
            .ToListAsync();

        if (matches.Count == 0)
        {
            return $"Error: no rule, spell, or condition found with key \"{key}\". Keys are lowercase-hyphenated " +
                   "slugs, e.g. \"fire-bolt\", \"blinded\" — try again with a different key or ask the DM.";
        }

        // เผื่อ key ชนกันข้าม category (ไม่เคยเกิดจริงในข้อมูลตอนนี้ แต่ schema ไม่ได้บังคับว่าห้าม) —
        // คืนทุกอันที่เจอแทนที่จะสุ่มเลือกอันเดียวแบบเงียบ ๆ
        return string.Join("\n\n", matches.Select(m => $"[{m.Category}] {m.Title}\n{m.Content}"));
    }

    /// <summary>ค้นสอง source ตามลำดับความสำคัญ (ดู doc comment ของ `LookupMonsterTool`): แม่แบบของ
    /// แคมเปญนี้ก่อน (DM อาจปรับ HP/AC เองแล้ว) ตกลงมาที่ SRD ต้นฉบับถ้าแคมเปญนี้ยังไม่มีแม่แบบนั้น —
    /// ไม่ throw เด็ดขาด เหมือน `ExecuteLookupRule`</summary>
    private static async Task<string> ExecuteLookupMonster(DnDEzDbContext db, Guid campaignId, JsonElement input)
    {
        string? key;
        try
        {
            key = input.GetProperty("key").GetString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return "Error: missing or malformed required \"key\" field.";
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return "Error: \"key\" must be a non-empty lowercase-hyphenated slug, e.g. \"goblin-warrior\".";
        }

        var normalizedKey = key.Trim().ToLowerInvariant();

        var campaignOverride = await db.MonsterStatBlocks.AsNoTracking()
            .Where(m => m.CampaignId == campaignId && m.SrdKey == normalizedKey)
            .Select(m => new { m.Name, m.ArmorClass, m.HitPoints, m.SpeedFeet, m.ReachFeet, m.Actions })
            .SingleOrDefaultAsync();
        if (campaignOverride is not null)
        {
            return $"{campaignOverride.Name} (adjusted by this campaign's DM, not the original SRD numbers): " +
                   $"AC {campaignOverride.ArmorClass}, HP {campaignOverride.HitPoints}, " +
                   $"Speed {campaignOverride.SpeedFeet} ft, Reach {campaignOverride.ReachFeet} ft.\n" +
                   $"Actions: {campaignOverride.Actions}";
        }

        var srd = await db.SrdMonsters.AsNoTracking()
            .Where(m => m.Key == normalizedKey)
            .Select(m => new
            {
                m.Name, m.Size, m.Type, m.Alignment, m.ArmorClass, m.HitPoints,
                m.SpeedFeet, m.ReachFeet, m.ChallengeRating, m.Data,
            })
            .SingleOrDefaultAsync();
        if (srd is null)
        {
            return $"Error: no monster found with key \"{key}\" in the SRD or this campaign's monster templates.";
        }

        return $"{srd.Name} (SRD default — no campaign-specific override) — {srd.Size} {srd.Type}, {srd.Alignment}. " +
               $"AC {srd.ArmorClass}, HP {srd.HitPoints}, Speed {srd.SpeedFeet} ft, Reach {srd.ReachFeet} ft, " +
               $"CR {srd.ChallengeRating}.\nFull stat block (traits/actions/abilities): {srd.Data}";
    }

    /// <summary>จำนวนบันทึกสูงสุดที่คืนให้ Claude ต่อการค้นหนึ่งครั้ง — กันคำค้นกว้าง ๆ ดูดบันทึกทั้ง
    /// แคมเปญเข้า context ทีเดียว (BYOK: DM จ่ายค่า token เอง)</summary>
    private const int MaxJournalSearchResults = 5;

    /// <summary>
    /// ค้นบันทึกแคมเปญ (Part D) — **กรองด้วย <paramref name="campaignId"/> ของเทิร์นนี้เสมอ** ไม่ใช่
    /// เชื่อว่า RLS จะกันให้ (RLS ของ `campaign_journal_entries` เป็น `dm_only` ผูกกับ
    /// `app_current_campaign()` ซึ่งกันอยู่แล้วอีกชั้น — แต่ tool นี้ถูกเรียกจาก path ของผู้เล่น
    /// (`/actions`) ได้ด้วย จึงต้องกรองเองให้ชัดที่ชั้น query ไม่พึ่งชั้นเดียว)
    ///
    /// ⚠️ **escape `%`/`_` ก่อนยัดเข้า ILIKE เสมอ** — ถ้าไม่ escape คำค้น `%` ตัวเดียวจะ match ทุกแถว
    /// กลายเป็นช่องดูดบันทึกทั้งแคมเปญ (รวมความลับ) ออกมาด้วยคำค้นเดียว · ไม่ throw เด็ดขาด ตาม
    /// convention เดียวกับ `ExecuteLookupRule`/`ExecuteLookupMonster`
    /// </summary>
    private static async Task<string> ExecuteSearchCampaignJournal(
        DnDEzDbContext db, Guid campaignId, JsonElement input)
    {
        string? query;
        try
        {
            query = input.GetProperty("query").GetString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return "Error: missing or malformed required \"query\" field.";
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: \"query\" must be a non-empty string, e.g. a character or place name.";
        }

        // `\` ต้องมาก่อนเสมอ ไม่งั้นจะไป escape backslash ที่ตัวเองเพิ่งใส่เข้าไปซ้ำ
        var escaped = query.Trim()
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        var pattern = $"%{escaped}%";

        var matches = await db.CampaignJournalEntries.AsNoTracking()
            .Where(e => e.CampaignId == campaignId
                && (EF.Functions.ILike(e.Title, pattern, "\\") || EF.Functions.ILike(e.Content, pattern, "\\")))
            .OrderByDescending(e => e.CreatedAt)
            .Take(MaxJournalSearchResults)
            .Select(e => new { e.Title, e.Content, e.AuthoredBy, e.CreatedAt })
            .ToListAsync();

        if (matches.Count == 0)
        {
            return $"Error: no journal entry in this campaign matches \"{query}\". " +
                   "Try a different word, or tell the DM nothing was recorded about it.";
        }

        var rendered = matches.Select(m =>
            $"[{m.CreatedAt:yyyy-MM-dd} · written by {m.AuthoredBy}] {m.Title}\n{m.Content}");
        return string.Join("\n\n", rendered);
    }
}

// PendingProposalPayload อยู่ใน ConsultEndpoints.cs (ประกาศไว้ก่อนไฟล์นี้ถูกแตกออกมา) — เพิ่ม field
// `SystemPrompt` ที่นั่นแล้วเช่นกัน (ดูเหตุผลใน doc comment ของ BuildSystemPromptAsync ด้านบน)

/// <summary>payload ที่ broadcast เข้า SignalR group ของแคมเปญ (ไม่ใช่ DM-only) ตอนมี Message ใหม่
/// จาก player-action turn — ดู doc comment ของคลาสว่าทำไม DM consult ไม่ broadcast แบบนี้</summary>
internal record ActionMessageCreatedBroadcast(
    Guid MessageId,
    Guid SessionId,
    string AuthorType,
    Guid? CharacterId,
    string Content,
    DateTimeOffset CreatedAt);

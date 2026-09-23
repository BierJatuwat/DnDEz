using System.Text.Json;
using DnDEz.Api.Endpoints;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Security;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Assistant;

/// <summary>
/// ตัววน loop ของผู้ช่วย DM (2026-09-23): เรียก Claude → รัน tool ที่ทำได้เลย → ส่งผลกลับ → วนต่อ จน Claude
/// ตอบจบ, เจอ tool ที่ต้องรอ DM อนุมัติ (หยุดพัก เก็บสถานะลง `assistant_runs`), หรือครบเพดานรอบ
///
/// ## commit ทีละขั้น
/// เรียก <see cref="RlsRequestTransaction.CheckpointAsync"/> ก่อนเรียก Claude ทุกรอบและหลังทุก tool ที่รัน —
/// ไม่ถือ row lock ข้ามช่วงรอ network และถ้ารอบหลังพัง ของที่ทำไปแล้ว (ซึ่งอาจ broadcast ถึงผู้เล่นแล้ว)
/// ไม่ถูก rollback ตาม · tool ที่พังกลางทาง rollback เฉพาะขั้นของตัวเอง (<see cref="RlsRequestTransaction.RollbackToCheckpointAsync"/>)
/// </summary>
internal sealed class AssistantRunner(
    DnDEzDbContext db,
    IAnthropicClient anthropicClient,
    RlsRequestTransaction requestTransaction,
    IServiceProvider services,
    ILogger logger)
{
    /// <summary>เพดานจำนวนครั้งที่เรียก Claude ต่อหนึ่งคำสั่ง (รวมรอบหลังอนุมัติ) — BYOK DM จ่ายเอง และกัน
    /// Claude วนเรียก tool ไม่รู้จบ</summary>
    internal const int MaxCallsPerRun = 8;

    internal const string RejectedResult = "The DM rejected this action. Do not retry it unless the DM asks again.";

    internal static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    // ── เริ่มคำสั่งใหม่ ─────────────────────────────────────────────────────

    internal async Task<AssistantTurnResponse> StartAsync(
        Campaign campaign, Guid dmUserId, Guid? sessionId, string content, string apiKey)
    {
        await AbandonPendingRunsAsync(campaign.Id, dmUserId);

        var offered = await OfferedToolsAsync(campaign.Id, sessionId);
        var approvalNames = offered.Where(o => o.NeedsApproval).Select(o => o.Tool.Name).ToList();
        var (system, activeBeatId) = await AssistantPrompt.BuildSystemPromptAsync(db, campaign, sessionId, approvalNames);

        var history = await AssistantPrompt.BuildHistoryAsync(db, campaign.Id, dmUserId);
        history = AppendUserText(history, content);

        db.ConsultMessages.Add(new ConsultMessage
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            DmUserId = dmUserId,
            Role = ConsultRole.Dm,
            Content = content,
            LinkedBeatId = activeBeatId,
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var state = new AssistantRunState
        {
            System = system,
            ActiveBeatId = activeBeatId,
            Messages = history,
        };
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            SessionId = sessionId,
            DmUserId = dmUserId,
            Status = AssistantRunStatus.Running,
            State = JsonSerializer.Serialize(state, StateJson),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AssistantRuns.Add(run);
        await db.SaveChangesAsync();

        return await ContinueAsync(run.Id, campaign, dmUserId, sessionId, state, apiKey);
    }

    // ── DM ตัดสินใจกับการ์ดอนุมัติหนึ่งใบ ───────────────────────────────────────

    internal async Task<(AssistantTurnResponse? Response, string? Error)> DecideAsync(
        Campaign campaign, Guid dmUserId, Guid runId, string toolUseId, bool approve, string? editedText, string apiKey)
    {
        // FOR UPDATE — สองรีเควสต์ตัดสินพร้อมกัน (กดรัว ๆ) ต้องต่อคิวกัน ไม่งั้นต่างคนต่างเขียนทับ state
        var run = await db.AssistantRuns
            .FromSqlInterpolated($"SELECT * FROM assistant_runs WHERE id = {runId} FOR UPDATE")
            .SingleOrDefaultAsync();
        if (run is null || run.CampaignId != campaign.Id || run.DmUserId != dmUserId)
        {
            return (null, "not_found");
        }

        if (run.Status != AssistantRunStatus.AwaitingApproval)
        {
            return (null, "not_awaiting_approval");
        }

        var state = JsonSerializer.Deserialize<AssistantRunState>(run.State, StateJson)!;
        var slot = state.CurrentSlots.FirstOrDefault(s => s.ToolUseId == toolUseId);
        if (slot is null || slot.Status != SlotStatus.AwaitingApproval)
        {
            return (null, "not_awaiting_approval");
        }

        if (approve)
        {
            if (editedText is not null)
            {
                if (slot.EditableField is null)
                {
                    return (null, "not_editable");
                }

                var trimmed = editedText.Trim();
                if (trimmed.Length == 0 || trimmed.Length > AssistantEndpoints.MaxContentLength)
                {
                    return (null, "invalid_text");
                }

                slot.Input = WithField(slot.Input, slot.EditableField, trimmed);
            }

            slot.Status = SlotStatus.Approved;
        }
        else
        {
            slot.Status = SlotStatus.Rejected;
            slot.Result = RejectedResult;
            slot.Summary = $"{slot.Description} — DM ปฏิเสธ";
        }

        if (state.CurrentSlots.Any(s => s.Status == SlotStatus.AwaitingApproval))
        {
            run.State = JsonSerializer.Serialize(state, StateJson);
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return (BuildResponse(run.Id, state, AssistantTurnStatus.AwaitingApproval, messageId: null), null);
        }

        // ครบทุกใบแล้ว — claim เป็น Running แล้ว commit ก่อนลงมือ: รีเควสต์ที่ต่อคิว FOR UPDATE อยู่จะเห็น
        // Running แล้วถอยไป (409) แทนที่จะรันงานเดียวกันซ้ำ
        run.Status = AssistantRunStatus.Running;
        run.State = JsonSerializer.Serialize(state, StateJson);
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await requestTransaction.CheckpointAsync();

        var ctx = new AssistantToolContext(db, campaign, run.SessionId, dmUserId, services);
        foreach (var approved in state.CurrentSlots.Where(s => s.Status == SlotStatus.Approved))
        {
            var tool = AssistantTools.Find(approved.Tool);
            await ExecuteSlotAsync(approved, tool!, ctx);
        }

        FlushSlots(state);
        return (await ContinueAsync(run.Id, campaign, dmUserId, run.SessionId, state, apiKey), null);
    }

    // ── loop ──────────────────────────────────────────────────────────────

    private async Task<AssistantTurnResponse> ContinueAsync(
        Guid runId, Campaign campaign, Guid dmUserId, Guid? sessionId, AssistantRunState state, string apiKey)
    {
        var ctx = new AssistantToolContext(db, campaign, sessionId, dmUserId, services);

        while (true)
        {
            if (state.CallsMade >= MaxCallsPerRun)
            {
                state.TextParts.Add(
                    $"(หยุดไว้ที่ {MaxCallsPerRun} ขั้นตามเพดานต่อคำสั่ง — ถ้างานยังไม่เสร็จ สั่งต่อได้เลยครับ)");
                return await CompleteAsync(runId, campaign.Id, dmUserId, sessionId, state);
            }

            var offered = await OfferedToolsAsync(campaign.Id, sessionId);

            // commit ก่อนรอ network ทุกครั้ง — ไม่ถือ lock/งานค้างไว้ข้ามช่วงรอ Claude
            await requestTransaction.CheckpointAsync();

            AgentTurnResult turn;
            try
            {
                turn = await anthropicClient.SendAgentTurnAsync(
                    apiKey, state.System, state.Messages, offered.Select(o => o.Tool.Definition).ToList(),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is AnthropicApiException or HttpRequestException or TaskCanceledException)
            {
                // ไม่ log ข้อความ/response — อาจมีเนื้อหาบทสนทนา (ดู doc comment ของ AnthropicClient)
                logger.LogWarning("Assistant run {RunId} stopped: Anthropic call failed ({Type})", runId, ex.GetType().Name);
                state.TextParts.Add("⚠️ เรียก AI ไม่สำเร็จ งานหยุดกลางทาง — สิ่งที่ทำไปแล้วด้านล่างยังมีผลอยู่ ลองสั่งใหม่อีกครั้งได้");
                return await CompleteAsync(runId, campaign.Id, dmUserId, sessionId, state);
            }

            state.CallsMade++;
            state.Messages.Add(new AgentMessage("assistant", turn.RawAssistantContent));
            if (!string.IsNullOrWhiteSpace(turn.Text))
            {
                state.TextParts.Add(turn.Text.Trim());
            }

            if (turn.ToolUses.Count == 0)
            {
                return await CompleteAsync(runId, campaign.Id, dmUserId, sessionId, state);
            }

            state.CurrentSlots = [];
            foreach (var toolUse in turn.ToolUses)
            {
                var slot = new ToolSlot
                {
                    ToolUseId = toolUse.Id,
                    Tool = toolUse.Name,
                    Input = toolUse.Input,
                };
                state.CurrentSlots.Add(slot);

                var offer = offered.FirstOrDefault(o => o.Tool.Name == toolUse.Name);
                if (offer is null)
                {
                    // Claude เรียก tool ที่ไม่ได้เสนอ (dial Manual / ไม่มีเซสชัน / ชื่อมั่ว) — ห้ามรันเด็ดขาด
                    slot.Description = toolUse.Name;
                    slot.Status = SlotStatus.Error;
                    slot.Result = $"Error: the tool \"{toolUse.Name}\" is not available right now.";
                    slot.Summary = $"{toolUse.Name} — ใช้ไม่ได้ตอนนี้";
                    continue;
                }

                slot.Description = offer.Tool.Describe(toolUse.Input);
                slot.EditableField = offer.Tool.EditableTextField;
                if (offer.NeedsApproval)
                {
                    slot.Status = SlotStatus.AwaitingApproval;
                    continue;
                }

                await ExecuteSlotAsync(slot, offer.Tool, ctx);
            }

            if (state.CurrentSlots.Any(s => s.Status == SlotStatus.AwaitingApproval))
            {
                await db.AssistantRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, AssistantRunStatus.AwaitingApproval)
                    .SetProperty(r => r.State, JsonSerializer.Serialize(state, StateJson))
                    .SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow));
                return BuildResponse(runId, state, AssistantTurnStatus.AwaitingApproval, messageId: null);
            }

            FlushSlots(state);
        }
    }

    private async Task ExecuteSlotAsync(ToolSlot slot, AssistantTool tool, AssistantToolContext ctx)
    {
        try
        {
            var outcome = await tool.Execute(ctx, slot.Input);
            slot.Status = outcome.IsError ? SlotStatus.Error : SlotStatus.Done;
            slot.Result = outcome.ResultForClaude;
            slot.Summary = outcome.Summary;
            await requestTransaction.CheckpointAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.PostgresException)
        {
            logger.LogWarning(ex, "Assistant tool {Tool} failed", tool.Name);
            db.ChangeTracker.Clear();
            await requestTransaction.RollbackToCheckpointAsync();
            slot.Status = SlotStatus.Error;
            slot.Result = "Error: the action failed on the server and was not applied.";
            slot.Summary = $"{slot.Description} — ทำไม่สำเร็จ";
        }
    }

    /// <summary>ส่งผลของ tool ทุกตัวในรอบนั้นกลับเป็น tool_result ตามลำดับเดิม (Anthropic บังคับให้ครบทุกตัว)
    /// แล้วย้ายเข้า activity</summary>
    private static void FlushSlots(AssistantRunState state)
    {
        var blocks = state.CurrentSlots.Select(s => (object)new
        {
            type = "tool_result",
            tool_use_id = s.ToolUseId,
            content = s.Result ?? "Error: no result.",
            is_error = s.Status is SlotStatus.Error or SlotStatus.Rejected,
        }).ToList();
        state.Messages.Add(new AgentMessage("user", JsonSerializer.SerializeToElement(blocks)));

        foreach (var s in state.CurrentSlots)
        {
            state.Activity.Add(new AssistantActivity(s.ToolUseId, s.Tool, s.Summary ?? s.Description, s.Status));
        }

        state.CurrentSlots = [];
    }

    private async Task<AssistantTurnResponse> CompleteAsync(
        Guid runId, Guid campaignId, Guid dmUserId, Guid? sessionId, AssistantRunState state)
    {
        var message = new ConsultMessage
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            DmUserId = dmUserId,
            Role = ConsultRole.Ai,
            Content = state.TextParts.Count == 0 ? "เสร็จแล้วครับ" : string.Join("\n\n", state.TextParts),
            LinkedBeatId = state.ActiveBeatId,
            SessionId = sessionId,
            Activity = AssistantActivityJson.Serialize(state.Activity),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ConsultMessages.Add(message);
        await db.SaveChangesAsync();

        // ล้าง state ทิ้ง — บทสนทนาดิบมี system prompt ที่รวมความลับ ไม่มีเหตุผลต้องเก็บหลังงานจบ
        await db.AssistantRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, AssistantRunStatus.Completed)
            .SetProperty(r => r.State, "{}")
            .SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow));

        return BuildResponse(runId, state, AssistantTurnStatus.Completed, message.Id, message.Content);
    }

    /// <summary>DM พิมพ์คำสั่งใหม่ทั้งที่ยังมีการ์ดรออนุมัติ = ทิ้งงานเดิม · เก็บสิ่งที่ทำไปแล้วเป็นข้อความในแชท
    /// ไม่งั้นประวัติจะหายไปเฉย ๆ ทั้งที่บาง tool รันไปแล้วจริง</summary>
    private async Task AbandonPendingRunsAsync(Guid campaignId, Guid dmUserId)
    {
        var pending = await db.AssistantRuns
            .Where(r => r.CampaignId == campaignId && r.DmUserId == dmUserId && r.Status == AssistantRunStatus.AwaitingApproval)
            .ToListAsync();
        foreach (var run in pending)
        {
            var state = JsonSerializer.Deserialize<AssistantRunState>(run.State, StateJson)!;
            foreach (var s in state.CurrentSlots)
            {
                var status = s.Status is SlotStatus.AwaitingApproval or SlotStatus.Approved ? SlotStatus.Cancelled : s.Status;
                state.Activity.Add(new AssistantActivity(s.ToolUseId, s.Tool, s.Summary ?? s.Description, status));
            }

            state.TextParts.Add("(งานนี้ถูกยกเลิกเพราะมีคำสั่งใหม่ก่อนตัดสินใจครบ)");
            db.ConsultMessages.Add(new ConsultMessage
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                DmUserId = dmUserId,
                Role = ConsultRole.Ai,
                Content = string.Join("\n\n", state.TextParts),
                LinkedBeatId = state.ActiveBeatId,
                SessionId = run.SessionId,
                Activity = AssistantActivityJson.Serialize(state.Activity),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            run.Status = AssistantRunStatus.Abandoned;
            run.State = "{}";
            run.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<ToolOffer>> OfferedToolsAsync(Guid campaignId, Guid? sessionId)
    {
        var levels = new Dictionary<AutonomySubsystem, AutonomyLevel>();
        var offers = new List<ToolOffer>();
        foreach (var tool in AssistantTools.All)
        {
            if (tool.RequiresSession && sessionId is null)
            {
                continue;
            }

            if (tool.Gate == ToolGate.Immediate)
            {
                offers.Add(new ToolOffer(tool, NeedsApproval: false));
                continue;
            }

            var subsystem = tool.Subsystem!.Value;
            if (!levels.TryGetValue(subsystem, out var level))
            {
                level = await OrchestratorTurnRunner.GetEffectiveAutonomyLevelAsync(db, campaignId, subsystem);
                levels[subsystem] = level;
            }

            if (level == AutonomyLevel.Manual)
            {
                continue;
            }

            var needsApproval = tool.Gate == ToolGate.AlwaysConfirmText || level == AutonomyLevel.Suggest;
            offers.Add(new ToolOffer(tool, needsApproval));
        }

        return offers;
    }

    internal static AssistantTurnResponse BuildResponse(
        Guid runId, AssistantRunState state, string status, Guid? messageId, string? text = null)
    {
        var activity = state.Activity
            .Concat(state.CurrentSlots
                .Where(s => s.Status != SlotStatus.AwaitingApproval)
                .Select(s => new AssistantActivity(s.ToolUseId, s.Tool, s.Summary ?? s.Description, s.Status)))
            .ToList();
        var pending = state.CurrentSlots
            .Where(s => s.Status == SlotStatus.AwaitingApproval)
            .Select(s => new PendingApprovalItem(
                s.ToolUseId, s.Tool, s.Description,
                s.EditableField is null ? null : AssistantTools.Str(s.Input, s.EditableField)))
            .ToList();
        return new AssistantTurnResponse(
            status, runId, messageId, text ?? string.Join("\n\n", state.TextParts), activity, pending);
    }

    private static List<AgentMessage> AppendUserText(List<AgentMessage> history, string content)
    {
        var element = JsonSerializer.SerializeToElement(content);
        if (history.Count > 0 && history[^1].Role == "user")
        {
            var merged = history[^1].Content.GetString() + "\n\n" + content;
            history[^1] = new AgentMessage("user", JsonSerializer.SerializeToElement(merged));
            return history;
        }

        history.Add(new AgentMessage("user", element));
        return history;
    }

    private static JsonElement WithField(JsonElement input, string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText())?.AsObject()
            ?? new System.Text.Json.Nodes.JsonObject();
        node[field] = value;
        return JsonSerializer.SerializeToElement(node);
    }

    private sealed record ToolOffer(AssistantTool Tool, bool NeedsApproval);
}

internal static class SlotStatus
{
    internal const string Done = "done";
    internal const string Error = "error";
    internal const string AwaitingApproval = "awaiting_approval";
    internal const string Approved = "approved";
    internal const string Rejected = "rejected";
    internal const string Cancelled = "cancelled";
}

internal static class AssistantTurnStatus
{
    internal const string Completed = "completed";
    internal const string AwaitingApproval = "awaiting_approval";
}

/// <summary>เก็บใน `assistant_runs.state` (jsonb) ระหว่างหยุดรอ DM — มี system prompt ที่รวมความลับ</summary>
internal sealed class AssistantRunState
{
    public string System { get; set; } = string.Empty;
    public Guid? ActiveBeatId { get; set; }
    public List<AgentMessage> Messages { get; set; } = [];
    public int CallsMade { get; set; }
    public List<string> TextParts { get; set; } = [];
    public List<AssistantActivity> Activity { get; set; } = [];
    public List<ToolSlot> CurrentSlots { get; set; } = [];
}

internal sealed class ToolSlot
{
    public string ToolUseId { get; set; } = string.Empty;
    public string Tool { get; set; } = string.Empty;
    public JsonElement Input { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = SlotStatus.AwaitingApproval;
    public string? Result { get; set; }
    public string? Summary { get; set; }
    public string? EditableField { get; set; }
}

internal sealed record AssistantActivity(string ToolUseId, string Tool, string Summary, string Status);

internal sealed record PendingApprovalItem(string ToolUseId, string Tool, string Description, string? EditableText);

internal sealed record AssistantTurnResponse(
    string Status,
    Guid RunId,
    Guid? MessageId,
    string Text,
    IReadOnlyList<AssistantActivity> Activity,
    IReadOnlyList<PendingApprovalItem> Pending);

internal static class AssistantActivityJson
{
    internal static List<AssistantActivity> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<AssistantActivity>>(json, AssistantRunner.StateJson) ?? [];

    internal static string? Serialize(IReadOnlyList<AssistantActivity> activity) =>
        activity.Count == 0 ? null : JsonSerializer.Serialize(activity, AssistantRunner.StateJson);
}

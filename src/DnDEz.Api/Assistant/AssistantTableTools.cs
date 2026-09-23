using System.Text.Json;
using DnDEz.Api.Endpoints;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using static DnDEz.Api.Assistant.AssistantTools;

namespace DnDEz.Api.Assistant;

/// <summary>
/// tool ที่ส่งของถึงผู้เล่น (Phase 3, 2026-09-23) — จุดเดียวที่ความลับในบริบทของผู้ช่วย DM มีทางไปถึงผู้เล่น
///
/// - `narrate_to_players` / `npc_say`: **AI แต่งข้อความเอง** → <see cref="ToolGate.AlwaysConfirmText"/> — DM เห็น
///   ข้อความจริงและแก้ได้ก่อนกดส่งทุกครั้ง แม้ dial เป็น Autonomous (Manual = ไม่เสนอเลย) · สิ่งที่ถูกส่งคือข้อความ
///   ที่ DM อนุมัติ (รวมที่แก้แล้ว) ไม่ใช่ที่ AI ร่างมา
/// - `generate_session_recap`: ฉบับผู้เล่นสร้างจาก pipeline แยกที่ไม่มีความลับตั้งแต่ต้น (`SessionRecapEndpoints`)
///   ผู้ช่วยไม่ได้เขียนเอง → ปลอดภัยโดยโครงสร้าง จึงใช้ dial Narration ตามปกติ
/// </summary>
internal static class AssistantTableTools
{
    private const string TextField = "text";

    private static readonly AnthropicToolDefinition NarrateTool = new(
        Name: "narrate_to_players",
        Description: "Send narration to every player at the table (it appears in their action feed). The DM always " +
                     "reviews and may edit your exact text before it is sent. Write only what the characters can " +
                     "perceive — never include secrets, DM notes or hidden stats.",
        InputSchema: new
        {
            type = "object",
            properties = new { text = new { type = "string", description = "The narration, at most 4000 characters." } },
            required = new[] { TextField },
        });

    private static readonly AnthropicToolDefinition NpcSayTool = new(
        Name: "npc_say",
        Description: "Have a DM-controlled NPC or monster say something to the players (shown with its name in their " +
                     "action feed). The DM always reviews and may edit your exact text before it is sent. Never " +
                     "include secrets the NPC would not reveal.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                character_id = new { type = "string", description = "Id of a DM-controlled character from get_game_state." },
                text = new { type = "string", description = "What the NPC says, at most 4000 characters." },
            },
            required = new[] { "character_id", TextField },
        });

    private static readonly AnthropicToolDefinition GenerateRecapTool = new(
        Name: "generate_session_recap",
        Description: "Write the selected session's recap: a private DM version and a \"previously on...\" player version " +
                     "that players see. Replaces the session's existing recap.",
        InputSchema: new { type = "object", properties = new { } });

    internal static readonly IReadOnlyList<AssistantTool> Tools =
    [
        new(NarrateTool, ToolGate.AlwaysConfirmText, AutonomySubsystem.Narration, RequiresSession: true,
            Sync(_ => "บรรยายถึงผู้เล่นทุกคน"), NarrateAsync, EditableTextField: TextField),
        new(NpcSayTool, ToolGate.AlwaysConfirmText, AutonomySubsystem.NpcDialogue, RequiresSession: true,
            DescribeNpcSayAsync, NpcSayAsync, EditableTextField: TextField),
        new(GenerateRecapTool, ToolGate.Dial, AutonomySubsystem.Narration, RequiresSession: true,
            Sync(_ => "สร้างสรุปเซสชันนี้ (ฉบับผู้เล่นจะขึ้นเป็น \"ตอนที่แล้ว…\" ให้ผู้เล่นเห็น)"), GenerateRecapAsync),
    ];

    private static async Task<ToolOutcome> NarrateAsync(AssistantToolContext ctx, JsonElement input)
    {
        var text = Str(input, TextField).Trim();
        if (text.Length == 0 || text.Length > AssistantEndpoints.MaxContentLength)
        {
            return new ToolOutcome("Error: text must be 1-4000 characters.", "บรรยายไม่สำเร็จ — ข้อความไม่ถูกต้อง", IsError: true);
        }

        await PublishAsync(ctx, text, characterId: null);
        return new ToolOutcome("Narration sent to the players.", "บรรยายถึงผู้เล่นแล้ว");
    }

    private static async Task<ToolOutcome> NpcSayAsync(AssistantToolContext ctx, JsonElement input)
    {
        var text = Str(input, TextField).Trim();
        if (text.Length == 0 || text.Length > AssistantEndpoints.MaxContentLength)
        {
            return new ToolOutcome("Error: text must be 1-4000 characters.", "บทพูด NPC ไม่สำเร็จ — ข้อความไม่ถูกต้อง", IsError: true);
        }

        // ⚠️ id มาจาก AI — ต้องเป็นตัวละครของแคมเปญนี้ **และ DM เป็นคนคุม** ไม่งั้น AI จะพูดแทนตัวละครผู้เล่นได้
        // ซึ่งขัดกฎตายตัวใน PROJECT-CONTEXT §2 ("AI ห้ามสั่ง action แทนตัวละครผู้เล่น")
        var npc = Guid.TryParse(Str(input, "character_id"), out var id)
            ? await ctx.Db.Characters.AsNoTracking()
                .Where(c => c.Id == id && c.CampaignId == ctx.Campaign.Id && c.ControlledByDm)
                .Select(c => new { c.Id, c.Name })
                .SingleOrDefaultAsync()
            : null;
        if (npc is null)
        {
            return new ToolOutcome(
                "Error: character_id must be a DM-controlled NPC or monster in this campaign.",
                "บทพูด NPC ไม่สำเร็จ — ไม่ใช่ NPC ของแคมเปญนี้", IsError: true);
        }

        await PublishAsync(ctx, text, npc.Id);
        return new ToolOutcome($"{npc.Name} said it to the players.", $"{npc.Name} พูดกับผู้เล่นแล้ว");
    }

    private static async Task<ToolOutcome> GenerateRecapAsync(AssistantToolContext ctx, JsonElement input)
    {
        var anthropicClient = ctx.Services.GetRequiredService<IAnthropicClient>();
        OpResult<RecapSnapshot> result;
        try
        {
            result = await SessionRecapEndpoints.GenerateAsync(
                ctx.Db, anthropicClient, ctx.ApiKey, ctx.Campaign.Id, ctx.SessionId!.Value);
        }
        catch (Exception ex) when (ex is AnthropicApiException or HttpRequestException or TaskCanceledException)
        {
            return new ToolOutcome("Error: the AI call for the recap failed; nothing was saved.", "สร้างสรุปไม่สำเร็จ — เรียก AI ไม่ได้", IsError: true);
        }

        if (!result.IsOk)
        {
            return new ToolOutcome(
                "Error: this session has no messages to summarise yet.", "สร้างสรุปไม่สำเร็จ — เซสชันยังไม่มีบทสนทนา", IsError: true);
        }

        var player = string.IsNullOrWhiteSpace(result.Value!.PlayerVersionText)
            ? "(empty — players have not seen anything in this session yet)"
            : result.Value.PlayerVersionText;
        return new ToolOutcome($"Recap saved. Player version:\n{player}", "สร้างสรุปเซสชันแล้ว");
    }

    private static async Task<string> DescribeNpcSayAsync(AssistantToolContext ctx, JsonElement input)
    {
        var name = Guid.TryParse(Str(input, "character_id"), out var id)
            ? await ctx.Db.Characters.AsNoTracking()
                .Where(c => c.Id == id && c.CampaignId == ctx.Campaign.Id)
                .Select(c => c.Name)
                .SingleOrDefaultAsync()
            : null;
        return $"{name ?? "(ไม่พบตัวละคร)"} พูดกับผู้เล่น";
    }

    /// <summary>
    /// เขียนเป็นข้อความสาธารณะของเซสชัน (`dm_private = false`) แล้ว broadcast แบบเดียวกับ narration ของเทิร์นผู้เล่น
    /// — ผู้เล่นเห็นในฟีด และเทิร์นผู้เล่นถัดไปได้เป็น history (ถูกต้อง เพราะ DM อนุมัติข้อความนี้ให้ทุกคนเห็นแล้ว)
    /// </summary>
    private static async Task PublishAsync(AssistantToolContext ctx, string text, Guid? characterId)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            SessionId = ctx.SessionId!.Value,
            AuthorType = MessageAuthorType.Ai,
            AuthorUserId = null,
            CharacterId = characterId,
            Content = text,
            ToolCalls = null,
            Source = ActionSource.Ai,
            DmPrivate = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Messages.Add(message);
        await ctx.Db.SaveChangesAsync();

        var hub = ctx.Services.GetRequiredService<IHubContext<CampaignHub>>();
        var logger = ctx.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AssistantTableTools));
        await OrchestratorTurnRunner.BroadcastActionMessageAsync(hub, logger, ctx.Campaign.Id, message);
    }
}

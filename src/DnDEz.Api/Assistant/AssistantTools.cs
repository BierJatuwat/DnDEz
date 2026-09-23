using System.Text;
using System.Text.Json;
using DnDEz.Api.Endpoints;
using DnDEz.Api.Orchestration;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Assistant;

/// <summary>
/// การขออนุญาตของ tool แต่ละตัว — **กำแพงความปลอดภัยของผู้ช่วย DM อยู่ที่นี่ ไม่ใช่ที่ช่องแชท**
/// (ดู doc comment ของ <see cref="AssistantPrompt"/>)
/// </summary>
internal enum ToolGate
{
    /// <summary>อ่านอย่างเดียว หรือเขียนข้อมูลส่วนตัวของ DM ที่แก้/ลบทีหลังได้ — ทำทันที</summary>
    Immediate,

    /// <summary>เปลี่ยนสถานะเกมแบบมีโครงสร้าง (AI ไม่ได้แต่งข้อความเอง) — ตาม autonomy dial ของ
    /// <see cref="AssistantTool.Subsystem"/>: Manual = ไม่เสนอ · Suggest = หยุดรออนุมัติ · Autonomous = ทำเลย</summary>
    Dial,

    /// <summary>
    /// AI แต่งข้อความเองแล้วส่งถึงผู้เล่น — **หยุดให้ DM เห็นข้อความจริงแล้วยืนยันทุกครั้ง แม้ dial เป็น Autonomous**
    /// (Manual ยังไม่เสนอเหมือนเดิม) · เป็นทางเดียวที่ความลับในบริบทของผู้ช่วยรั่วถึงผู้เล่นได้ และเป็นที่
    /// prompt injection จากข้อความผู้เล่นจะไปออกฤทธิ์ — dial ห้ามข้ามกฎนี้เด็ดขาด
    /// </summary>
    AlwaysConfirmText,
}

internal sealed record AssistantToolContext(
    DnDEzDbContext Db, Campaign Campaign, Guid? SessionId, Guid DmUserId, IServiceProvider Services);

/// <summary><see cref="ResultForClaude"/> คือสิ่งที่ส่งกลับเป็น tool_result · <see cref="Summary"/> คือบรรทัด
/// ภาษาคนที่หน้าจอ DM โชว์ใน "สิ่งที่ทำไปแล้ว"</summary>
internal sealed record ToolOutcome(string ResultForClaude, string Summary, bool IsError = false);

internal sealed record AssistantTool(
    AnthropicToolDefinition Definition,
    ToolGate Gate,
    AutonomySubsystem? Subsystem,
    bool RequiresSession,
    Func<JsonElement, string> Describe,
    Func<AssistantToolContext, JsonElement, Task<ToolOutcome>> Execute,
    string? EditableTextField = null)
{
    public string Name => Definition.Name;
}

internal static class AssistantTools
{
    private const int MaxJournalTitle = 200;
    private const int MaxJournalContent = 8000;

    // ── definitions ──────────────────────────────────────────────────────

    private static readonly AnthropicToolDefinition GetGameStateTool = new(
        Name: "get_game_state",
        Description: "Get the current state of this campaign with the ids you need for other tools: every " +
                     "character (player characters and DM-controlled NPCs/monsters) with HP/AC, the campaign's " +
                     "monster templates, and — when a session is selected — the active combat encounter and the " +
                     "active map scene with its tokens.",
        InputSchema: new { type = "object", properties = new { } });

    private static readonly AnthropicToolDefinition WriteJournalTool = new(
        Name: "write_journal_entry",
        Description: "Write a new entry in the campaign journal (private to the DM). Use it to remember " +
                     "decisions, NPC details, plot threads or anything the DM asks you to note down.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string", description = "Short title, at most 200 characters." },
                content = new { type = "string", description = "The note itself, at most 8000 characters." },
            },
            required = new[] { "title", "content" },
        });

    private static readonly AnthropicToolDefinition UpdateJournalTool = new(
        Name: "update_journal_entry",
        Description: "Replace the title and/or content of an existing campaign journal entry. Get the entry id " +
                     "from search_campaign_journal first.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                entry_id = new { type = "string", description = "The journal entry id." },
                title = new { type = "string" },
                content = new { type = "string" },
            },
            required = new[] { "entry_id" },
        });

    internal static readonly IReadOnlyList<AssistantTool> All =
    [
        ReadOnly(OrchestratorTurnRunner.LookupRuleTool, input => $"ค้นกฎ {Str(input, "key")}"),
        ReadOnly(OrchestratorTurnRunner.LookupMonsterTool, input => $"ค้นสเตต {Str(input, "key")}"),
        ReadOnly(OrchestratorTurnRunner.SearchCampaignJournalTool, input => $"ค้นบันทึก \"{Str(input, "query")}\""),

        new(GetGameStateTool, ToolGate.Immediate, null, RequiresSession: false,
            _ => "ดูสถานะเกม", ExecuteGetGameStateAsync),

        new(WriteJournalTool, ToolGate.Immediate, null, RequiresSession: false,
            input => $"เขียนบันทึก \"{Str(input, "title")}\"", ExecuteWriteJournalAsync),
        new(UpdateJournalTool, ToolGate.Immediate, null, RequiresSession: false,
            input => "แก้บันทึกแคมเปญ", ExecuteUpdateJournalAsync),

        new(OrchestratorTurnRunner.RollDiceTool, ToolGate.Dial, AutonomySubsystem.RulesCombat, RequiresSession: true,
            input => $"ทอยเต๋า {Str(input, "expression")}", ExecuteRollDiceAsync),
    ];

    internal static AssistantTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    // ── executors ────────────────────────────────────────────────────────

    private static AssistantTool ReadOnly(AnthropicToolDefinition definition, Func<JsonElement, string> describe) =>
        new(definition, ToolGate.Immediate, null, RequiresSession: false, describe, async (ctx, input) =>
        {
            var result = await OrchestratorTurnRunner.ExecuteReadOnlyToolAsync(
                ctx.Db, ctx.Campaign.Id, definition.Name, input);
            var isError = result.StartsWith("Error", StringComparison.Ordinal);
            return new ToolOutcome(result, isError ? $"{describe(input)} — ไม่พบ" : describe(input), isError);
        });

    private static async Task<ToolOutcome> ExecuteGetGameStateAsync(AssistantToolContext ctx, JsonElement input)
    {
        var db = ctx.Db;
        var sb = new StringBuilder();

        var characters = await db.Characters.AsNoTracking()
            .Where(c => c.CampaignId == ctx.Campaign.Id)
            .OrderBy(c => c.ControlledByDm).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.CurrentHp, c.MaxHp, c.ArmorClass, c.ControlledByDm, c.SpawnedFromMonsterId })
            .ToListAsync();
        sb.AppendLine("Characters:");
        foreach (var c in characters)
        {
            var kind = !c.ControlledByDm ? "player character"
                : c.SpawnedFromMonsterId is not null ? "spawned monster (removed when its encounter ends)"
                : "DM-controlled NPC";
            sb.AppendLine($"- id={c.Id} {c.Name} [{kind}] HP {c.CurrentHp}/{c.MaxHp} AC {c.ArmorClass}");
        }

        var monsters = await db.MonsterStatBlocks.AsNoTracking()
            .Where(m => m.CampaignId == ctx.Campaign.Id)
            .OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.ArmorClass, m.HitPoints })
            .ToListAsync();
        sb.AppendLine(monsters.Count == 0 ? "Monster templates: none yet." : "Monster templates:");
        foreach (var m in monsters)
        {
            sb.AppendLine($"- id={m.Id} {m.Name} AC {m.ArmorClass} HP {m.HitPoints}");
        }

        if (ctx.SessionId is null)
        {
            sb.AppendLine("No session selected: no encounter or map information.");
            return new ToolOutcome(sb.ToString(), "ดูสถานะเกม");
        }

        var encounter = await db.Encounters.AsNoTracking()
            .Where(e => e.SessionId == ctx.SessionId && e.EndedAt == null)
            .Select(e => new { e.Id, e.CurrentParticipantId, e.TurnNumber })
            .SingleOrDefaultAsync();
        if (encounter is null)
        {
            sb.AppendLine("Encounter: none running.");
        }
        else
        {
            sb.AppendLine($"Encounter id={encounter.Id} (turn number {encounter.TurnNumber}), initiative order:");
            var participants = await db.EncounterParticipants.AsNoTracking()
                .Where(p => p.EncounterId == encounter.Id)
                .OrderBy(p => p.TurnOrder)
                .Join(db.Characters, p => p.CharacterId, c => c.Id, (p, c) => new { p.Id, c.Name, p.InitiativeTotal })
                .ToListAsync();
            foreach (var p in participants)
            {
                var current = p.Id == encounter.CurrentParticipantId ? " <- current turn" : string.Empty;
                sb.AppendLine($"- {p.Name} (initiative {p.InitiativeTotal}){current}");
            }
        }

        var scene = await db.Scenes.AsNoTracking()
            .Where(s => s.SessionId == ctx.SessionId && s.IsActive)
            .Select(s => new { s.Id, s.Name, s.Tokens })
            .SingleOrDefaultAsync();
        if (scene is null)
        {
            sb.AppendLine("Map: no active scene.");
        }
        else
        {
            var tokens = SceneTokenSerialization.Parse(scene.Tokens);
            var names = characters.ToDictionary(c => c.Id, c => c.Name);
            foreach (var m in monsters)
            {
                names.TryAdd(m.Id, m.Name);
            }

            sb.AppendLine($"Map scene id={scene.Id} \"{scene.Name}\" with {tokens.Count} token(s) (grid cells):");
            foreach (var t in tokens)
            {
                var name = names.GetValueOrDefault(t.EntityId, "unknown");
                sb.AppendLine($"- token id={t.Id} {name} [{t.EntityType}] at ({t.X}, {t.Y})");
            }
        }

        return new ToolOutcome(sb.ToString(), "ดูสถานะเกม");
    }

    private static async Task<ToolOutcome> ExecuteWriteJournalAsync(AssistantToolContext ctx, JsonElement input)
    {
        var title = Str(input, "title").Trim();
        var content = Str(input, "content").Trim();
        if (title.Length == 0 || content.Length == 0 || title.Length > MaxJournalTitle || content.Length > MaxJournalContent)
        {
            return new ToolOutcome(
                $"Error: title (1-{MaxJournalTitle} chars) and content (1-{MaxJournalContent} chars) are required.",
                "เขียนบันทึกไม่สำเร็จ — ข้อมูลไม่ครบ", IsError: true);
        }

        ctx.Db.CampaignJournalEntries.Add(new CampaignJournalEntry
        {
            Id = Guid.NewGuid(),
            CampaignId = ctx.Campaign.Id,
            SessionId = ctx.SessionId,
            Title = title,
            Content = content,
            AuthoredBy = JournalAuthor.Ai,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();
        return new ToolOutcome("Journal entry saved.", $"เขียนบันทึก \"{title}\"");
    }

    private static async Task<ToolOutcome> ExecuteUpdateJournalAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (!Guid.TryParse(Str(input, "entry_id"), out var entryId))
        {
            return new ToolOutcome("Error: entry_id must be a journal entry id.", "แก้บันทึกไม่สำเร็จ — id ไม่ถูกต้อง", IsError: true);
        }

        // ⚠️ id มาจาก AI = input ภายนอก (prompt injection พาให้ส่ง id ข้ามแคมเปญได้) — ผูก CampaignId เสมอ
        var entry = await ctx.Db.CampaignJournalEntries
            .SingleOrDefaultAsync(e => e.Id == entryId && e.CampaignId == ctx.Campaign.Id);
        if (entry is null)
        {
            return new ToolOutcome("Error: no journal entry with that id in this campaign.", "แก้บันทึกไม่สำเร็จ — ไม่พบ", IsError: true);
        }

        var title = input.TryGetProperty("title", out _) ? Str(input, "title").Trim() : entry.Title;
        var content = input.TryGetProperty("content", out _) ? Str(input, "content").Trim() : entry.Content;
        if (title.Length == 0 || content.Length == 0 || title.Length > MaxJournalTitle || content.Length > MaxJournalContent)
        {
            return new ToolOutcome(
                $"Error: title must be 1-{MaxJournalTitle} chars and content 1-{MaxJournalContent} chars.",
                "แก้บันทึกไม่สำเร็จ — ข้อมูลไม่ถูกต้อง", IsError: true);
        }

        entry.Title = title;
        entry.Content = content;
        await ctx.Db.SaveChangesAsync();
        return new ToolOutcome("Journal entry updated.", $"แก้บันทึก \"{title}\"");
    }

    private static async Task<ToolOutcome> ExecuteRollDiceAsync(AssistantToolContext ctx, JsonElement input)
    {
        var result = OrchestratorTurnRunner.ExecuteRollDice(ctx.Db, ctx.SessionId!.Value, input);
        await ctx.Db.SaveChangesAsync();
        var isError = result.StartsWith("Error", StringComparison.Ordinal);
        return new ToolOutcome(result, isError ? "ทอยเต๋าไม่สำเร็จ" : $"ทอยเต๋า {Str(input, "expression")} → {result}", isError);
    }

    internal static string Str(JsonElement input, string property) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

using System.Text;
using System.Text.Json;
using DnDEz.Api.Encounters;
using DnDEz.Api.Endpoints;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using static DnDEz.Api.Assistant.AssistantTools;

namespace DnDEz.Api.Assistant;

/// <summary>
/// tool คุมเกมของผู้ช่วย DM (Phase 2, 2026-09-23) — เตรียมมอนสเตอร์, ต่อสู้, แผนที่
///
/// ทุกตัวเรียก operation ตัวเดียวกับ endpoint ที่ DM กดเองบนหน้าจอ (`OpResult` ใน `Endpoints/`) — logic อยู่ที่เดียว
/// ผู้เล่นเห็นผลแบบเดียวกับที่ DM กดเอง (broadcast เดิม) · id ทุกตัวที่ Claude ส่งมาถือเป็น input ภายนอก:
/// operation ผูก campaignId/sceneId ที่ผู้ช่วยได้มาจากฝั่ง server เสมอ ไม่ใช่จากที่ Claude ส่งมา
/// </summary>
internal static class AssistantGameTools
{
    private const int MaxSearchResults = 10;

    // ── definitions ──────────────────────────────────────────────────────

    private static readonly AnthropicToolDefinition SearchSrdMonstersTool = new(
        Name: "search_srd_monsters",
        Description: "Search the SRD 5.2.1 monster library by English name to find the key needed for " +
                     "add_monster_from_srd and lookup_monster. Returns up to 10 matches with CR, AC and HP.",
        InputSchema: new
        {
            type = "object",
            properties = new { query = new { type = "string", description = "Part of the English name, e.g. \"goblin\"." } },
            required = new[] { "query" },
        });

    private static readonly AnthropicToolDefinition AddMonsterFromSrdTool = new(
        Name: "add_monster_from_srd",
        Description: "Copy a monster from the SRD library into this campaign as a template (players cannot see " +
                     "templates). Returns the template id. If it is already in the campaign, returns the existing one.",
        InputSchema: new
        {
            type = "object",
            properties = new { key = new { type = "string", description = "SRD key from search_srd_monsters." } },
            required = new[] { "key" },
        });

    private static readonly AnthropicToolDefinition UpdateMonsterStatsTool = new(
        Name: "update_monster_stats",
        Description: "Change a campaign monster template's name or stats. Omit fields you do not want to change. " +
                     "Monsters already spawned keep their old stats.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                monster_id = new { type = "string", description = "Template id from get_game_state." },
                name = new { type = "string" },
                armor_class = new { type = "integer", minimum = 1, maximum = 99 },
                hit_points = new { type = "integer", minimum = 1, maximum = 9999 },
                speed_feet = new { type = "integer", minimum = 0, maximum = 999 },
                reach_feet = new { type = "integer", minimum = 5, maximum = 999 },
            },
            required = new[] { "monster_id" },
        });

    private static readonly AnthropicToolDefinition SpawnMonstersTool = new(
        Name: "spawn_monsters",
        Description: "Create 1-20 individual copies of a monster template, each with its own HP, so they can join " +
                     "combat. Returns their character ids. They are removed when their encounter ends. By default " +
                     "players cannot see their HP/AC.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                monster_id = new { type = "string", description = "Template id from get_game_state or add_monster_from_srd." },
                count = new { type = "integer", minimum = 1, maximum = 20 },
                stats_visible_to_party = new { type = "boolean", description = "Show HP/AC to players. Default false." },
            },
            required = new[] { "monster_id", "count" },
        });

    private static readonly AnthropicToolDefinition StartEncounterTool = new(
        Name: "start_encounter",
        Description: "Start a combat encounter in the selected session and roll initiative for the given characters " +
                     "(player characters, NPCs and spawned monsters). Only one encounter can run at a time.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                character_ids = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Character ids from get_game_state or spawn_monsters (1-20, no duplicates).",
                },
            },
            required = new[] { "character_ids" },
        });

    private static readonly AnthropicToolDefinition NextTurnTool = new(
        Name: "next_turn",
        Description: "Advance the running encounter to the next creature in initiative order.",
        InputSchema: new { type = "object", properties = new { } });

    private static readonly AnthropicToolDefinition EndEncounterTool = new(
        Name: "end_encounter",
        Description: "End the running encounter. Spawned monsters and their map tokens are removed and a short " +
                     "summary is written to the campaign journal.",
        InputSchema: new { type = "object", properties = new { } });

    private static readonly AnthropicToolDefinition PlaceTokenTool = new(
        Name: "place_token",
        Description: "Place a token on the active map scene for a character (player, NPC or spawned monster) or a " +
                     "monster template. Coordinates are grid cells.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                entity_id = new { type = "string", description = "Character id or monster template id." },
                x = new { type = "number" },
                y = new { type = "number" },
            },
            required = new[] { "entity_id", "x", "y" },
        });

    private static readonly AnthropicToolDefinition MoveTokenTool = new(
        Name: "move_token",
        Description: "Move a token on the active map scene to a grid cell (a free DM move, not a rules-checked move).",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                token_id = new { type = "string", description = "Token id from get_game_state." },
                x = new { type = "number" },
                y = new { type = "number" },
            },
            required = new[] { "token_id", "x", "y" },
        });

    private static readonly AnthropicToolDefinition RemoveTokenTool = new(
        Name: "remove_token",
        Description: "Remove a token from the active map scene.",
        InputSchema: new
        {
            type = "object",
            properties = new { token_id = new { type = "string", description = "Token id from get_game_state." } },
            required = new[] { "token_id" },
        });

    private static readonly AnthropicToolDefinition RevealFogTool = new(
        Name: "reveal_fog",
        Description: "Reveal a circular area of the active map to the players (they can then see tokens inside it). " +
                     "Coordinates and radius are in grid cells.",
        InputSchema: new
        {
            type = "object",
            properties = new
            {
                x = new { type = "number" },
                y = new { type = "number" },
                radius = new { type = "number", exclusiveMinimum = 0 },
            },
            required = new[] { "x", "y", "radius" },
        });

    private static readonly AnthropicToolDefinition HideFogTool = new(
        Name: "hide_fog",
        Description: "Cover a previously revealed fog area again.",
        InputSchema: new
        {
            type = "object",
            properties = new { area_id = new { type = "string", description = "Revealed area id from get_game_state." } },
            required = new[] { "area_id" },
        });

    internal static readonly IReadOnlyList<AssistantTool> Tools =
    [
        new(SearchSrdMonstersTool, ToolGate.Immediate, null, RequiresSession: false,
            Sync(input => $"ค้นคลัง SRD \"{Str(input, "query")}\""), SearchSrdMonstersAsync),
        new(AddMonsterFromSrdTool, ToolGate.Immediate, null, RequiresSession: false,
            Sync(input => $"เพิ่ม {Str(input, "key")} เข้าแคมเปญ"), AddMonsterFromSrdAsync),
        new(UpdateMonsterStatsTool, ToolGate.Dial, AutonomySubsystem.RulesCombat, RequiresSession: false,
            DescribeUpdateMonsterAsync, UpdateMonsterStatsAsync),
        new(SpawnMonstersTool, ToolGate.Dial, AutonomySubsystem.RulesCombat, RequiresSession: false,
            DescribeSpawnAsync, SpawnMonstersAsync),

        new(StartEncounterTool, ToolGate.Dial, AutonomySubsystem.RulesCombat, RequiresSession: true,
            DescribeStartEncounterAsync, StartEncounterAsync),
        new(NextTurnTool, ToolGate.Dial, AutonomySubsystem.RulesCombat, RequiresSession: true,
            Sync(_ => "เลื่อนไปเทิร์นถัดไป"), NextTurnAsync),
        new(EndEncounterTool, ToolGate.Dial, AutonomySubsystem.RulesCombat, RequiresSession: true,
            Sync(_ => "จบไฟต์ (มอนสเตอร์ที่ spawn จะถูกลบพร้อมสรุปผลลงบันทึก)"), EndEncounterAsync),

        new(PlaceTokenTool, ToolGate.Dial, AutonomySubsystem.MapScene, RequiresSession: true,
            async (ctx, input) => $"วาง {await EntityNameAsync(ctx, GuidOf(input, "entity_id"))} ที่ช่อง ({Num(input, "x")}, {Num(input, "y")})",
            PlaceTokenAsync),
        new(MoveTokenTool, ToolGate.Dial, AutonomySubsystem.MapScene, RequiresSession: true,
            async (ctx, input) => $"ย้าย {await TokenNameAsync(ctx, GuidOf(input, "token_id"))} ไปช่อง ({Num(input, "x")}, {Num(input, "y")})",
            MoveTokenAsync),
        new(RemoveTokenTool, ToolGate.Dial, AutonomySubsystem.MapScene, RequiresSession: true,
            async (ctx, input) => $"เอา {await TokenNameAsync(ctx, GuidOf(input, "token_id"))} ออกจากแผนที่",
            RemoveTokenAsync),
        new(RevealFogTool, ToolGate.Dial, AutonomySubsystem.MapScene, RequiresSession: true,
            Sync(input => $"เปิดหมอกให้ผู้เล่นเห็นรอบช่อง ({Num(input, "x")}, {Num(input, "y")}) รัศมี {Num(input, "radius")} ช่อง"),
            RevealFogAsync),
        new(HideFogTool, ToolGate.Dial, AutonomySubsystem.MapScene, RequiresSession: true,
            Sync(_ => "ปิดหมอกพื้นที่ที่เคยเปิดไว้"), HideFogAsync),
    ];

    // ── เตรียมมอนสเตอร์ ──────────────────────────────────────────────────

    private static async Task<ToolOutcome> SearchSrdMonstersAsync(AssistantToolContext ctx, JsonElement input)
    {
        var query = Str(input, "query").Trim();
        if (query.Length == 0 || query.Length > 100)
        {
            return Fail("query must be 1-100 characters.", "ค้นคลัง SRD ไม่สำเร็จ");
        }

        // escape wildcard ของ ILIKE — แบบเดียวกับ search_campaign_journal
        var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var matches = await ctx.Db.SrdMonsters.AsNoTracking()
            .Where(m => EF.Functions.ILike(m.Name, pattern, "\\"))
            .OrderBy(m => m.Name)
            .Take(MaxSearchResults)
            .Select(m => new { m.Key, m.Name, m.ChallengeRating, m.ArmorClass, m.HitPoints })
            .ToListAsync();
        if (matches.Count == 0)
        {
            return new ToolOutcome($"Error: no SRD monster matches \"{query}\".", $"ค้นคลัง SRD \"{query}\" — ไม่พบ", IsError: true);
        }

        var sb = new StringBuilder();
        foreach (var m in matches)
        {
            sb.AppendLine($"- key={m.Key} {m.Name} (CR {m.ChallengeRating}, AC {m.ArmorClass}, HP {m.HitPoints})");
        }

        return new ToolOutcome(sb.ToString(), $"ค้นคลัง SRD \"{query}\" — เจอ {matches.Count} ตัว");
    }

    private static async Task<ToolOutcome> AddMonsterFromSrdAsync(AssistantToolContext ctx, JsonElement input)
    {
        var result = await SrdEndpoints.CopySrdMonsterAsync(ctx.Db, ctx.Campaign.Id, Str(input, "key"));
        if (!result.IsOk)
        {
            return FromFailure(result.Status, result.Error, "เพิ่มมอนสเตอร์ไม่สำเร็จ");
        }

        var v = result.Value!;
        return new ToolOutcome(
            $"Template id={v.MonsterId} {v.Name} ({(v.Created ? "added" : "was already in the campaign")}).",
            v.Created ? $"เพิ่ม {v.Name} เข้าแคมเปญ" : $"{v.Name} อยู่ในแคมเปญแล้ว");
    }

    private static async Task<ToolOutcome> UpdateMonsterStatsAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (GuidOf(input, "monster_id") is not { } monsterId)
        {
            return Fail("monster_id must be a template id.", "ปรับค่าพลังไม่สำเร็จ — id ไม่ถูกต้อง");
        }

        var request = new UpdateMonsterRequest(
            input.TryGetProperty("name", out _) ? Str(input, "name") : null,
            Int(input, "armor_class"), Int(input, "hit_points"), Int(input, "speed_feet"), Int(input, "reach_feet"));
        var result = await MonsterEndpoints.UpdateMonsterAsync(ctx.Db, ctx.Campaign.Id, monsterId, request);
        if (!result.IsOk)
        {
            return FromFailure(result.Status, result.Error, "ปรับค่าพลังไม่สำเร็จ");
        }

        var m = result.Value!;
        return new ToolOutcome(
            $"Template id={m.Id} {m.Name}: AC {m.ArmorClass}, HP {m.HitPoints}, speed {m.SpeedFeet} ft, reach {m.ReachFeet} ft.",
            $"ปรับ {m.Name}: AC {m.ArmorClass} · HP {m.HitPoints}");
    }

    private static async Task<ToolOutcome> SpawnMonstersAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (GuidOf(input, "monster_id") is not { } monsterId)
        {
            return Fail("monster_id must be a template id.", "ส่งมอนสเตอร์ลงสนามไม่สำเร็จ — id ไม่ถูกต้อง");
        }

        var visible = input.TryGetProperty("stats_visible_to_party", out var v) && v.ValueKind == JsonValueKind.True;
        var result = await MonsterSpawnEndpoints.SpawnAsync(ctx.Db, ctx.Campaign.Id, monsterId, Int(input, "count") ?? 0, visible);
        if (!result.IsOk)
        {
            return FromFailure(result.Status, result.Error, "ส่งมอนสเตอร์ลงสนามไม่สำเร็จ");
        }

        var spawned = result.Value!;
        var lines = string.Join("\n", spawned.Select(s => $"- id={s.Id} {s.Name} HP {s.CurrentHp}/{s.MaxHp} AC {s.ArmorClass}"));
        return new ToolOutcome($"Spawned {spawned.Count}:\n{lines}", $"ส่ง {string.Join(", ", spawned.Select(s => s.Name))} ลงสนาม");
    }

    // ── ต่อสู้ ─────────────────────────────────────────────────────────────

    private static async Task<ToolOutcome> StartEncounterAsync(AssistantToolContext ctx, JsonElement input)
    {
        var ids = GuidList(input, "character_ids");
        if (ids is null)
        {
            return Fail("character_ids must be a list of character ids.", "เปิดไฟต์ไม่สำเร็จ — รายชื่อไม่ถูกต้อง");
        }

        var result = await EncounterEndpoints.StartEncounterAsync(
            ctx.Db, Service<IEncounterTurnStateStore>(ctx), Service<IHubContext<CampaignHub>>(ctx), Logger(ctx),
            ctx.Campaign.Id, ctx.SessionId!.Value, ids);
        if (result.Status == OpStatus.Conflict)
        {
            return Fail("an encounter is already running in this session; end it first.", "เปิดไฟต์ไม่สำเร็จ — มีไฟต์ค้างอยู่");
        }

        if (!result.IsOk)
        {
            return FromFailure(result.Status, result.Error, "เปิดไฟต์ไม่สำเร็จ");
        }

        var names = await NamesAsync(ctx, ids);
        var order = string.Join(", ", result.Value!.Participants.Select(p => names.GetValueOrDefault(p.CharacterId, "?")));
        return new ToolOutcome($"Encounter started. Initiative order: {order}.", $"เปิดไฟต์ — ลำดับ: {order}");
    }

    private static async Task<ToolOutcome> NextTurnAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (await ActiveEncounterIdAsync(ctx) is not { } encounterId)
        {
            return Fail("no encounter is running in this session.", "เลื่อนเทิร์นไม่สำเร็จ — ไม่มีไฟต์");
        }

        var result = await EncounterEndpoints.NextTurnAsync(
            ctx.Db, Service<IEncounterTurnStateStore>(ctx), Service<IHubContext<CampaignHub>>(ctx),
            Service<SceneTokenFlusher>(ctx), Logger(ctx), ctx.Campaign.Id, encounterId);
        if (!result.IsOk)
        {
            return FromFailure(result.Status, result.Error, "เลื่อนเทิร์นไม่สำเร็จ");
        }

        var name = (await NamesAsync(ctx, [result.Value!.CurrentCharacterId])).GetValueOrDefault(result.Value.CurrentCharacterId, "?");
        return new ToolOutcome($"It is now {name}'s turn.", $"เลื่อนเทิร์น — ตาของ {name}");
    }

    private static async Task<ToolOutcome> EndEncounterAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (await ActiveEncounterIdAsync(ctx) is not { } encounterId)
        {
            return Fail("no encounter is running in this session.", "จบไฟต์ไม่สำเร็จ — ไม่มีไฟต์");
        }

        var result = await EncounterEndpoints.EndEncounterAsync(
            ctx.Db, Service<IEncounterTurnStateStore>(ctx), Service<IHubContext<CampaignHub>>(ctx),
            Service<SceneTokenFlusher>(ctx), Service<ISceneTokenStore>(ctx), Logger(ctx), ctx.Campaign.Id, encounterId);
        return result.IsOk
            ? new ToolOutcome("Encounter ended. Spawned monsters were removed and a summary was written to the journal.", "จบไฟต์")
            : FromFailure(result.Status, result.Error, "จบไฟต์ไม่สำเร็จ");
    }

    // ── แผนที่ ─────────────────────────────────────────────────────────────

    private static async Task<ToolOutcome> PlaceTokenAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (await ActiveSceneIdAsync(ctx) is not { } sceneId)
        {
            return Fail("the selected session has no active map scene.", "วาง token ไม่สำเร็จ — ไม่มีแผนที่");
        }

        if (GuidOf(input, "entity_id") is not { } entityId || Dbl(input, "x") is not { } x || Dbl(input, "y") is not { } y)
        {
            return Fail("entity_id, x and y are required.", "วาง token ไม่สำเร็จ — ข้อมูลไม่ครบ");
        }

        // ให้ Claude ส่งแค่ id — ฝั่ง server ตัดสินเองว่าเป็นตัวละครหรือแม่แบบมอนสเตอร์ของแคมเปญนี้
        var entityType = await ctx.Db.Characters.AnyAsync(c => c.Id == entityId && c.CampaignId == ctx.Campaign.Id)
            ? "Character"
            : "Monster";
        var result = await SceneTokenEndpoints.CreateTokenAsync(
            ctx.Db, ctx.Campaign.Id, sceneId, ctx.DmUserId, entityType, entityId, x, y);
        if (!result.IsOk)
        {
            return FromFailure(result.Status, result.Error, "วาง token ไม่สำเร็จ");
        }

        var name = await EntityNameAsync(ctx, entityId);
        return new ToolOutcome($"Placed token id={result.Value!.Id} for {name} at ({x}, {y}).", $"วาง {name} ที่ช่อง ({x}, {y})");
    }

    private static async Task<ToolOutcome> MoveTokenAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (await ActiveSceneIdAsync(ctx) is not { } sceneId)
        {
            return Fail("the selected session has no active map scene.", "ย้าย token ไม่สำเร็จ — ไม่มีแผนที่");
        }

        if (GuidOf(input, "token_id") is not { } tokenId || Dbl(input, "x") is not { } x || Dbl(input, "y") is not { } y)
        {
            return Fail("token_id, x and y are required.", "ย้าย token ไม่สำเร็จ — ข้อมูลไม่ครบ");
        }

        var name = await TokenNameAsync(ctx, tokenId);
        var result = await SceneTokenEndpoints.MoveTokenAsync(
            ctx.Db, Service<ISceneTokenStore>(ctx), Logger(ctx), ctx.Campaign.Id, sceneId, tokenId, ctx.DmUserId, x, y);
        return result.IsOk
            ? new ToolOutcome($"Moved {name} to ({x}, {y}).", $"ย้าย {name} ไปช่อง ({x}, {y})")
            : FromFailure(result.Status, result.Error, "ย้าย token ไม่สำเร็จ");
    }

    private static async Task<ToolOutcome> RemoveTokenAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (await ActiveSceneIdAsync(ctx) is not { } sceneId)
        {
            return Fail("the selected session has no active map scene.", "เอา token ออกไม่สำเร็จ — ไม่มีแผนที่");
        }

        if (GuidOf(input, "token_id") is not { } tokenId)
        {
            return Fail("token_id is required.", "เอา token ออกไม่สำเร็จ — id ไม่ถูกต้อง");
        }

        var name = await TokenNameAsync(ctx, tokenId);
        var result = await SceneTokenEndpoints.DeleteTokenAsync(
            ctx.Db, Service<ISceneTokenStore>(ctx), ctx.Campaign.Id, sceneId, tokenId, ctx.DmUserId);
        return result.IsOk
            ? new ToolOutcome($"Removed {name} from the map.", $"เอา {name} ออกจากแผนที่")
            : FromFailure(result.Status, result.Error, "เอา token ออกไม่สำเร็จ");
    }

    private static async Task<ToolOutcome> RevealFogAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (await ActiveSceneIdAsync(ctx) is not { } sceneId)
        {
            return Fail("the selected session has no active map scene.", "เปิดหมอกไม่สำเร็จ — ไม่มีแผนที่");
        }

        if (Dbl(input, "x") is not { } x || Dbl(input, "y") is not { } y || Dbl(input, "radius") is not { } radius)
        {
            return Fail("x, y and radius are required.", "เปิดหมอกไม่สำเร็จ — ข้อมูลไม่ครบ");
        }

        var result = await SceneFogEndpoints.RevealAsync(
            ctx.Db, Service<IHubContext<CampaignHub>>(ctx), Logger(ctx), ctx.Campaign.Id, sceneId, x, y, radius);
        return result.IsOk
            ? new ToolOutcome($"Revealed area id={result.Value!.Id} at ({x}, {y}) radius {radius}.", $"เปิดหมอกรอบช่อง ({x}, {y}) รัศมี {radius}")
            : FromFailure(result.Status, result.Error, "เปิดหมอกไม่สำเร็จ");
    }

    private static async Task<ToolOutcome> HideFogAsync(AssistantToolContext ctx, JsonElement input)
    {
        if (await ActiveSceneIdAsync(ctx) is not { } sceneId)
        {
            return Fail("the selected session has no active map scene.", "ปิดหมอกไม่สำเร็จ — ไม่มีแผนที่");
        }

        if (GuidOf(input, "area_id") is not { } areaId)
        {
            return Fail("area_id is required.", "ปิดหมอกไม่สำเร็จ — id ไม่ถูกต้อง");
        }

        var result = await SceneFogEndpoints.HideAsync(
            ctx.Db, Service<IHubContext<CampaignHub>>(ctx), Logger(ctx), ctx.Campaign.Id, sceneId, areaId);
        return result.IsOk
            ? new ToolOutcome("Area covered again.", "ปิดหมอกพื้นที่ที่เคยเปิดไว้")
            : FromFailure(result.Status, result.Error, "ปิดหมอกไม่สำเร็จ");
    }

    // ── คำบรรยายบนการ์ดอนุมัติ (อ่านชื่อจาก DB แทนโชว์ id) ────────────────────────

    private static async Task<string> DescribeUpdateMonsterAsync(AssistantToolContext ctx, JsonElement input)
    {
        var name = await EntityNameAsync(ctx, GuidOf(input, "monster_id"));
        var changes = new List<string>();
        if (input.TryGetProperty("name", out _)) changes.Add($"ชื่อ → {Str(input, "name")}");
        if (Int(input, "armor_class") is { } ac) changes.Add($"AC {ac}");
        if (Int(input, "hit_points") is { } hp) changes.Add($"HP {hp}");
        if (Int(input, "speed_feet") is { } sp) changes.Add($"เดิน {sp} ฟุต");
        if (Int(input, "reach_feet") is { } re) changes.Add($"เอื้อม {re} ฟุต");
        return $"ปรับแม่แบบ {name}: {string.Join(" · ", changes)}";
    }

    private static async Task<string> DescribeSpawnAsync(AssistantToolContext ctx, JsonElement input)
    {
        var name = await EntityNameAsync(ctx, GuidOf(input, "monster_id"));
        var visible = input.TryGetProperty("stats_visible_to_party", out var v) && v.ValueKind == JsonValueKind.True;
        return $"ส่ง {name} ×{Int(input, "count")} ลงสนาม ({(visible ? "ผู้เล่นเห็น HP/AC" : "ซ่อน HP/AC จากผู้เล่น")})";
    }

    private static async Task<string> DescribeStartEncounterAsync(AssistantToolContext ctx, JsonElement input)
    {
        var ids = GuidList(input, "character_ids") ?? [];
        var names = await NamesAsync(ctx, ids);
        return $"เปิดไฟต์และทอย initiative: {string.Join(", ", ids.Select(id => names.GetValueOrDefault(id, "?")))}";
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<Guid?> ActiveSceneIdAsync(AssistantToolContext ctx) =>
        await ctx.Db.Scenes.AsNoTracking()
            .Where(s => s.SessionId == ctx.SessionId && s.IsActive)
            .Select(s => (Guid?)s.Id)
            .SingleOrDefaultAsync();

    private static async Task<Guid?> ActiveEncounterIdAsync(AssistantToolContext ctx) =>
        await ctx.Db.Encounters.AsNoTracking()
            .Where(e => e.SessionId == ctx.SessionId && e.CampaignId == ctx.Campaign.Id && e.EndedAt == null)
            .Select(e => (Guid?)e.Id)
            .SingleOrDefaultAsync();

    private static async Task<Dictionary<Guid, string>> NamesAsync(AssistantToolContext ctx, IReadOnlyCollection<Guid> ids) =>
        await ctx.Db.Characters.AsNoTracking()
            .Where(c => c.CampaignId == ctx.Campaign.Id && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

    /// <summary>ชื่อของตัวละครหรือแม่แบบมอนสเตอร์ในแคมเปญนี้ — ไม่เจอ = "(ไม่พบ)" (การ์ดอนุมัติต้องบอก DM ตรง ๆ
    /// ว่า id ที่ AI ส่งมาไม่มีอยู่จริง ไม่ใช่โชว์ id ดิบให้เดา)</summary>
    private static async Task<string> EntityNameAsync(AssistantToolContext ctx, Guid? id)
    {
        if (id is null)
        {
            return "(ไม่พบ)";
        }

        return await ctx.Db.Characters.AsNoTracking()
                   .Where(c => c.Id == id && c.CampaignId == ctx.Campaign.Id).Select(c => c.Name).SingleOrDefaultAsync()
               ?? await ctx.Db.MonsterStatBlocks.AsNoTracking()
                   .Where(m => m.Id == id && m.CampaignId == ctx.Campaign.Id).Select(m => m.Name).SingleOrDefaultAsync()
               ?? "(ไม่พบ)";
    }

    private static async Task<string> TokenNameAsync(AssistantToolContext ctx, Guid? tokenId)
    {
        if (tokenId is null || await ActiveSceneIdAsync(ctx) is not { } sceneId)
        {
            return "(ไม่พบ)";
        }

        var tokensJson = await ctx.Db.Scenes.AsNoTracking().Where(s => s.Id == sceneId).Select(s => s.Tokens).SingleAsync();
        var token = SceneTokenSerialization.Parse(tokensJson).FirstOrDefault(t => t.Id == tokenId);
        return token is null ? "(ไม่พบ)" : await EntityNameAsync(ctx, token.EntityId);
    }

    private static T Service<T>(AssistantToolContext ctx) where T : notnull => ctx.Services.GetRequiredService<T>();

    private static ILogger Logger(AssistantToolContext ctx) =>
        ctx.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AssistantGameTools));

    private static ToolOutcome Fail(string reason, string summary) => new($"Error: {reason}", summary, IsError: true);

    private static ToolOutcome FromFailure(OpStatus status, string? error, string summary) => status switch
    {
        OpStatus.NotFound => Fail("nothing with that id exists in this campaign/session.", $"{summary} — ไม่พบ"),
        OpStatus.Conflict => Fail("the game is not in a state that allows this right now.", $"{summary} — ทำตอนนี้ไม่ได้"),
        OpStatus.Forbidden => Fail("not allowed.", $"{summary} — ไม่มีสิทธิ์"),
        _ => Fail(error is null ? "invalid input." : $"invalid input ({error}).", $"{summary} — ข้อมูลไม่ถูกต้อง"),
    };

    internal static Guid? GuidOf(JsonElement input, string property) =>
        Guid.TryParse(Str(input, property), out var id) ? id : null;

    internal static int? Int(JsonElement input, string property) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n)
            ? n
            : null;

    internal static double? Dbl(JsonElement input, string property) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d)
        && double.IsFinite(d)
            ? d
            : null;

    private static string Num(JsonElement input, string property) => Dbl(input, property)?.ToString() ?? "?";

    private static List<Guid>? GuidList(JsonElement input, string property)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty(property, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ids = new List<Guid>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                return null;
            }

            ids.Add(id);
        }

        return ids;
    }
}

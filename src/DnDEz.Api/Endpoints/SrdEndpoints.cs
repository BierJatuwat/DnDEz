using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// ข้อความ attribution ที่ CC-BY-4.0 บังคับให้แสดงในงานที่ใช้เนื้อหา SRD 5.2.1 — ตัวบทตามหน้า "Legal Information" ของ
/// `docs/SRD_CC_v5.2.1.pdf` เป๊ะ ห้ามแก้ถ้อยคำ (ไฟล์ต้นฉบับห้ามใส่การอ้างอิง Wizards รูปแบบอื่นนอกจากข้อความนี้)
/// ทุก response ของ `/srd/*` แนบข้อความนี้มาด้วย เพื่อให้ frontend แสดงได้โดยไม่ต้อง hardcode ซ้ำ
/// </summary>
public static class SrdAttribution
{
    public const string Text =
        "This work includes material from the System Reference Document 5.2.1 (\"SRD 5.2.1\") by Wizards of the Coast LLC, "
        + "available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0 "
        + "International License, available at https://creativecommons.org/licenses/by/4.0/legalcode.";
}

/// <summary>
/// คลัง SRD 5.2.1 (มอนสเตอร์/เวท/condition/กฎ) — อ่านอย่างเดียวทั้งหมด ยกเว้นการ "คัดลอกมอนสเตอร์เข้าแคมเปญ"
/// ที่เป็น DM-only · ที่มาของข้อมูลและสิ่งที่ยังไม่ได้นำเข้าอยู่ใน doc comment ของ migration `ImportSrd521`
///
/// **ไม่ผูกแคมเปญ + ทุกคนที่ล็อกอินอ่านได้**: SRD เป็นเนื้อหา CC-BY สาธารณะ ไม่มีข้อมูลลับของแคมเปญใด (ต่างจาก
/// `monster_stat_blocks` ที่เป็นของ DM) — RLS ของตารางเป็น read_only และ role แอปเขียนไม่ได้ (ทดสอบแยกใน SrdImportTests)
/// </summary>
public static class SrdEndpoints
{
    private const int MaxQueryLength = 100;
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    public static void MapSrdEndpoints(this WebApplication app)
    {
        app.MapGet("/srd/attribution", () => Results.Ok(new { text = SrdAttribution.Text, license = "CC-BY-4.0" }))
            .AllowAnonymous();

        app.MapGet("/srd/monsters", async (
            string? query, string? type, string? crMax, string? lang, int? limit, int? offset,
            ClaimsPrincipal principal, DnDEzDbContext db) =>
        {
            if (principal.GetUserId() is null)
            {
                return Results.Unauthorized();
            }

            if (query is { Length: > MaxQueryLength } || type is { Length: > MaxQueryLength })
            {
                return Results.BadRequest(new { error = "query_too_long" });
            }

            var language = NormalizeLang(lang);
            if (language is null)
            {
                return Results.BadRequest(new { error = "invalid_lang" });
            }

            decimal? crCap = null;
            if (!string.IsNullOrWhiteSpace(crMax))
            {
                crCap = ParseChallengeRating(crMax);
                if (crCap is null)
                {
                    return Results.BadRequest(new { error = "invalid_cr" });
                }
            }

            // ตารางมี 330 แถวคงที่ (แก้ทีเดียวตอน migrate ไม่ใช่ทุก request) — ดึงทั้งก้อนมาแล้วกรอง/เรียง/
            // ผสมคำแปลในหน่วยความจำง่ายกว่าประกอบ query ที่รองรับทั้งสองภาษาที่ชั้น SQL โดยไม่เสียอะไร
            var rows = await db.SrdMonsters.AsNoTracking()
                .Select(m => new MonsterSummaryRow(m.Key, m.Name, m.Size, m.Type, m.Alignment, m.ArmorClass,
                    m.HitPoints, m.SpeedFeet, m.ReachFeet, m.ChallengeRating, m.Xp))
                .ToListAsync();

            var translations = language == Thai
                ? await db.SrdMonsterTranslations.AsNoTracking().Where(t => t.Locale == Thai)
                    .ToDictionaryAsync(t => t.MonsterKey)
                : new Dictionary<string, SrdMonsterTranslation>();

            var merged = rows.Select(r =>
            {
                translations.TryGetValue(r.Key, out var t);
                return new MonsterSummaryRow(
                    r.Key, t?.Name ?? r.Name, t?.Size ?? r.Size, t?.Type ?? r.Type, t?.Alignment ?? r.Alignment,
                    r.ArmorClass, r.HitPoints, r.SpeedFeet, r.ReachFeet, r.ChallengeRating, r.Xp);
            });

            if (!string.IsNullOrWhiteSpace(query))
            {
                var needle = query.Trim();
                merged = merged.Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                // ชนิดตรงตัว (ไม่สนตัวพิมพ์) — "Beast" ไม่ควรไปเจอ "Swarm of Tiny Beasts"
                var typeNeedle = type.Trim();
                merged = merged.Where(r => string.Equals(r.Type, typeNeedle, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = merged
                .Select(r => (Row: r, Cr: ParseChallengeRating(r.ChallengeRating) ?? 0m))
                .Where(x => crCap is null || x.Cr <= crCap)
                .OrderBy(x => x.Cr).ThenBy(x => x.Row.Name, StringComparer.Ordinal)
                .Select(x => x.Row)
                .ToList();

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(offset ?? 0, 0);
            return Results.Ok(new
            {
                total = filtered.Count,
                items = filtered.Skip(skip).Take(take),
                attribution = SrdAttribution.Text,
            });
        }).RequireAuthorization();

        app.MapGet("/srd/monsters/{key}", async (
            string key, string? lang, ClaimsPrincipal principal, DnDEzDbContext db) =>
        {
            if (principal.GetUserId() is null)
            {
                return Results.Unauthorized();
            }

            var language = NormalizeLang(lang);
            if (language is null)
            {
                return Results.BadRequest(new { error = "invalid_lang" });
            }

            var m = await db.SrdMonsters.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key);
            if (m is null)
            {
                return Results.NotFound();
            }

            SrdMonsterTranslation? t = language == Thai
                ? await db.SrdMonsterTranslations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.MonsterKey == key && x.Locale == Thai)
                : null;

            // `data` (traits/actions ฯลฯ) แปลแล้ว = แทนที่ทั้งก้อน (ไม่ผสมรายท่อน) เพราะคำแปลเต็มเก็บเป็น
            // stat block รูปทรงเดียวกับต้นฉบับทั้งใบ — ยังไม่แปล (t.Data เป็น null) = คืนอังกฤษของเดิม
            var contentTranslated = t?.Data is not null;
            var dataJson = contentTranslated ? t!.Data! : m.Data;

            return Results.Ok(new
            {
                m.Key,
                // Name: ชื่อมอนสเตอร์เป็น "ชื่อเฉพาะ" — ตามคำขอผู้ใช้ (2026-09-22) ไม่แปล `t.Name` เก็บค่า
                // อังกฤษไว้เหมือนกันทุกตัวอยู่แล้ว (ดู doc comment migration `AddSrdThaiTranslations`) จึง
                // ไม่ต่างจาก `m.Name` แต่คง `t?.Name ?? m.Name` ไว้เพื่อความสม่ำเสมอของ pattern merge
                Name = t?.Name ?? m.Name,
                Size = t?.Size ?? m.Size,
                Type = t?.Type ?? m.Type,
                Alignment = t?.Alignment ?? m.Alignment,
                m.ArmorClass, m.HitPoints, m.SpeedFeet, m.ReachFeet, m.ChallengeRating, m.Xp, m.SourceVersion,
                data = JsonDocument.Parse(dataJson).RootElement,
                // null เมื่อ language == "en" — คำถาม "แปลหรือยัง" ไม่มีความหมายตอนขอภาษาอังกฤษเอง (ซึ่งเป็น
                // ต้นฉบับอยู่แล้ว) ถ้าคืน false ไปตรง ๆ ทุกครั้ง client ที่เชื่อ flag นี้จะเข้าใจผิดว่า "ยังไม่แปล"
                // แม้ตอนที่ผู้ใช้ตั้งใจดูอังกฤษอยู่แล้ว (พบจริงตอนทดสอบ — ป้าย "ยังไม่แปล" ขึ้นทับแม้กำลังโชว์ EN)
                // ⚠️ ตั้งแต่ 2026-09-22 flag นี้ไม่ครอบคลุมชื่อ (ชื่อไม่แปลเสมอ) หมายถึงเฉพาะ
                // ขนาด/ชนิด/แนวคิด แปลแล้วหรือยัง — ปัจจุบันเป็น true เสมอเมื่อ lang=th (330/330 แถว)
                translated = language == Thai ? t is not null : (bool?)null,
                contentTranslated = language == Thai ? contentTranslated : (bool?)null,
                attribution = SrdAttribution.Text,
            });
        }).RequireAuthorization();

        // category: spell | condition | rule — ค้นตามชื่อ (ILIKE) · สำหรับเวทกรองระดับ/คลาสเพิ่มได้ (อ่านจาก data)
        //
        // ⚠️ `level` เป็นตัวเลข (ไม่ต้องแปล) แต่ `class` เป็นข้อความที่ผู้ใช้พิมพ์ — เมื่อ lang=th ต้องเทียบกับ
        // ชื่อคลาสภาษาไทยที่ผสมมาแล้ว (frontend ส่ง "จอมเวทย์" ไม่ใช่ "Wizard") ไม่ใช่เทียบกับ data อังกฤษดิบ
        app.MapGet("/srd/corpus", async (
            string? category, string? query, int? level, string? @class, string? lang, int? limit, int? offset,
            ClaimsPrincipal principal, DnDEzDbContext db) =>
        {
            if (principal.GetUserId() is null)
            {
                return Results.Unauthorized();
            }

            if (query is { Length: > MaxQueryLength } || @class is { Length: > MaxQueryLength })
            {
                return Results.BadRequest(new { error = "query_too_long" });
            }

            if (category is not null && category is not ("spell" or "condition" or "rule"))
            {
                return Results.BadRequest(new { error = "invalid_category" });
            }

            var language = NormalizeLang(lang);
            if (language is null)
            {
                return Results.BadRequest(new { error = "invalid_lang" });
            }

            var q = db.RulesCorpusEntries.AsNoTracking().AsQueryable();
            if (category is not null)
            {
                q = q.Where(e => e.Category == category);
            }

            // ตาราง global คงที่ (380 แถวรวมทุก category) — กรอง title/level/class ในหน่วยความจำหลังผสมคำแปล
            // ด้วยเหตุผลเดียวกับ /srd/monsters ด้านบน (รองรับค้นสองภาษาโดยไม่ต้อง join ที่ชั้น SQL)
            var rows = await q.OrderBy(e => e.Title)
                .Select(e => new CorpusRow(e.Category, e.Key, e.Title, e.Data)).ToListAsync();

            var translations = language == Thai
                ? await db.RulesCorpusTranslations.AsNoTracking().Where(t => t.Locale == Thai)
                    .ToDictionaryAsync(t => (t.Category, t.Key))
                : new Dictionary<(string, string), RulesCorpusTranslation>();

            var merged = rows.Select(r =>
            {
                translations.TryGetValue((r.Category, r.Key), out var t);
                return (Row: r, Translation: t, Data: MergeData(r.Data, t?.Data));
            });

            if (!string.IsNullOrWhiteSpace(query))
            {
                var needle = query.Trim();
                merged = merged.Where(x =>
                    (x.Translation?.Title ?? x.Row.Title).Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            if (level is not null || !string.IsNullOrWhiteSpace(@class))
            {
                merged = merged.Where(x => MatchesSpellFilter(x.Data, level, @class));
            }

            var list = merged.ToList();
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(offset ?? 0, 0);
            return Results.Ok(new
            {
                total = list.Count,
                items = list.Skip(skip).Take(take).Select(x => new
                {
                    x.Row.Category, x.Row.Key, Title = x.Translation?.Title ?? x.Row.Title, data = x.Data,
                    // null เมื่อ language == "en" — เหตุผลเดียวกับที่ /srd/monsters/{key} ทำ (ดู comment ที่นั่น)
                    contentTranslated = language == Thai ? x.Translation?.Content is not null : (bool?)null,
                }),
                attribution = SrdAttribution.Text,
            });
        }).RequireAuthorization();

        app.MapGet("/srd/corpus/{category}/{key}", async (
            string category, string key, string? lang, ClaimsPrincipal principal, DnDEzDbContext db) =>
        {
            if (principal.GetUserId() is null)
            {
                return Results.Unauthorized();
            }

            var language = NormalizeLang(lang);
            if (language is null)
            {
                return Results.BadRequest(new { error = "invalid_lang" });
            }

            var e = await db.RulesCorpusEntries.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Category == category && x.Key == key);
            if (e is null)
            {
                return Results.NotFound();
            }

            RulesCorpusTranslation? t = language == Thai
                ? await db.RulesCorpusTranslations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Category == category && x.Key == key && x.Locale == Thai)
                : null;

            var contentTranslated = t?.Content is not null;

            return Results.Ok(new
            {
                e.Category,
                e.Key,
                // Title: ชื่อเวท/condition/rule เป็น "ชื่อเฉพาะ" — ตามคำขอผู้ใช้ (2026-09-22) ไม่แปล
                // `t.Title` เก็บค่าอังกฤษไว้เหมือนกันทุกแถวอยู่แล้ว จึงไม่ต่างจาก `e.Title`
                Title = t?.Title ?? e.Title,
                Content = contentTranslated ? t!.Content! : e.Content,
                e.License,
                e.Source,
                data = MergeData(e.Data, t?.Data),
                // null เมื่อ language == "en" — เหตุผลเดียวกับที่ /srd/monsters/{key} ทำ (ดู comment ที่นั่น)
                // ⚠️ ไม่ครอบคลุมชื่อ/หัวข้อ (ไม่แปลเสมอ) — หมายถึง school/classes (เวท) แปลแล้วหรือยัง
                translated = language == Thai ? t is not null : (bool?)null,
                contentTranslated = language == Thai ? contentTranslated : (bool?)null,
                attribution = SrdAttribution.Text,
            });
        }).RequireAuthorization();

        // DM เลือกมอนสเตอร์จากคลัง → คัดลอกเป็นแม่แบบของแคมเปญ (แล้ววาง token จากแม่แบบนั้นได้ตามปกติ)
        // ทำซ้ำได้ปลอดภัย: มีสำเนาของ key นี้ในแคมเปญอยู่แล้ว = คืนตัวเดิม (200) ไม่สร้างซ้ำ
        app.MapPost("/campaigns/{campaignId:guid}/monsters/from-srd", async (
            Guid campaignId, CopySrdMonsterRequest? request,
            ClaimsPrincipal principal, DnDEzDbContext db) =>
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

            var result = await CopySrdMonsterAsync(db, campaignId, request?.Key);
            return result.Status switch
            {
                OpStatus.Ok when result.Value!.Created => Results.Created(
                    $"/campaigns/{campaignId}/monsters/{result.Value.MonsterId}", result.Value),
                OpStatus.Ok => Results.Ok(result.Value),
                OpStatus.NotFound => Results.NotFound(),
                _ => Results.BadRequest(new { error = result.Error }),
            };
        }).RequireAuthorization();
    }

    /// <summary>คัดลอกมอนสเตอร์จากคลัง SRD เข้าแคมเปญ (มีอยู่แล้ว = คืนตัวเดิม `Created: false`) — ใช้ร่วมกันระหว่าง
    /// endpoint กับ tool `add_monster_from_srd` ของผู้ช่วย DM · ผู้เรียกต้องเช็คสิทธิ์ DM มาก่อน</summary>
    internal static async Task<OpResult<CopySrdMonsterResponse>> CopySrdMonsterAsync(
        DnDEzDbContext db, Guid campaignId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            return OpResult<CopySrdMonsterResponse>.Bad("invalid_key");
        }

        var srd = await db.SrdMonsters.AsNoTracking().SingleOrDefaultAsync(m => m.Key == key);
        if (srd is null)
        {
            return OpResult<CopySrdMonsterResponse>.NotFound();
        }

        var existing = await db.MonsterStatBlocks
            .SingleOrDefaultAsync(m => m.CampaignId == campaignId && m.SrdKey == srd.Key);
        if (existing is not null)
        {
            return OpResult<CopySrdMonsterResponse>.Ok(new CopySrdMonsterResponse(existing.Id, srd.Key, existing.Name, Created: false));
        }

        var block = new MonsterStatBlock
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Name = srd.Name,
            ArmorClass = srd.ArmorClass,
            HitPoints = srd.HitPoints,
            SpeedFeet = srd.SpeedFeet,
            ReachFeet = srd.ReachFeet,
            Actions = BuildActionsJson(srd.Data),
            // เนื้อหา SRD ผ่านการตรวจตอน import แล้ว (ดู migration ImportSrd521) จึงไม่ต้องรอ DM review เหมือน
            // source import — กฎ "review_status != approved ห้ามเข้า orchestrator" ยังคงบังคับตามเดิม
            ReviewStatus = ReviewStatus.Approved,
            SrdKey = srd.Key,
        };
        db.MonsterStatBlocks.Add(block);
        await db.SaveChangesAsync();

        return OpResult<CopySrdMonsterResponse>.Ok(new CopySrdMonsterResponse(block.Id, srd.Key, block.Name, Created: true));
    }

    /// <summary>รวม traits/actions/bonusActions/reactions/legendaryActions เป็นรายการเดียวที่มี kind กำกับ</summary>
    internal static string BuildActionsJson(string srdData)
    {
        using var doc = JsonDocument.Parse(srdData);
        var kinds = new (string Property, string Kind)[]
        {
            ("traits", "Trait"), ("actions", "Action"), ("bonusActions", "BonusAction"),
            ("reactions", "Reaction"), ("legendaryActions", "LegendaryAction"),
        };
        var items = new List<object>();
        foreach (var (property, kind) in kinds)
        {
            if (!doc.RootElement.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in arr.EnumerateArray())
            {
                items.Add(new
                {
                    kind,
                    name = entry.GetProperty("name").GetString() ?? "",
                    text = entry.GetProperty("text").GetString() ?? "",
                });
            }
        }

        return JsonSerializer.Serialize(items);
    }

    /// <summary>"1/8" → 0.125 · "0" → 0 · "None" (stat block ของเวทเรียกอัญเชิญ) → null</summary>
    internal static decimal? ParseChallengeRating(string cr)
    {
        var s = cr.Trim();
        var slash = s.IndexOf('/');
        if (slash > 0)
        {
            return decimal.TryParse(s[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && decimal.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
                && d > 0
                ? n / d
                : null;
        }

        return decimal.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) ? whole : null;
    }

    /// <summary>ตัวย่อ locale ภาษาไทยที่ตาราง *_translations ใช้เก็บ — ค่าเดียวที่รองรับตอนนี้นอกจากอังกฤษ</summary>
    private const string Thai = "th";

    /// <summary>"" / null → "en" (ค่าเริ่มต้นเดิมของ API ก่อนมีคำแปล ห้ามเปลี่ยนเพื่อไม่ให้ผู้เรียกเดิมพัง) ·
    /// "en"/"th" → ค่านั้นตรง ๆ · อย่างอื่น → null ให้ caller ตอบ 400 (กันพิมพ์ผิดเงียบ ๆ เช่น "TH"/"thai")</summary>
    private static string? NormalizeLang(string? lang) =>
        string.IsNullOrEmpty(lang) ? "en" : lang is "en" or Thai ? lang : null;

    /// <summary>ผสม data ภาษาอังกฤษกับคำแปล — คำแปลมีฟิลด์ไหนแทนที่ทั้งฟิลด์นั้น (ไม่ไล่ผสมทีละสมาชิกใน array
    /// เช่น "actions" ถูกแทนที่ทั้งก้อนด้วยเวอร์ชันแปล ไม่ใช่จับคู่ทีละท่า) ฟิลด์ที่ไม่มีในคำแปล (เช่น "level"
    /// ของเวทซึ่งเป็นตัวเลข ไม่ต้องแปล) ยังคงเป็นค่าอังกฤษเดิม</summary>
    private static JsonObject MergeData(string englishJson, string? translationJson)
    {
        var merged = JsonNode.Parse(englishJson)!.AsObject();
        if (translationJson is not null)
        {
            var overlay = JsonNode.Parse(translationJson)!.AsObject();
            foreach (var (k, v) in overlay)
            {
                merged[k] = v?.DeepClone();
            }
        }

        return merged;
    }

    private static bool MatchesSpellFilter(JsonObject data, int? level, string? cls)
    {
        if (level is not null)
        {
            if (data["level"] is not JsonValue levelValue || !levelValue.TryGetValue<int>(out var l) || l != level)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(cls))
        {
            if (data["classes"] is not JsonArray classes)
            {
                return false;
            }

            return classes.Any(c => string.Equals(c?.GetValue<string>(), cls.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    private sealed record MonsterSummaryRow(
        string Key, string Name, string Size, string Type, string Alignment, int ArmorClass, int HitPoints,
        int SpeedFeet, int ReachFeet, string ChallengeRating, int Xp);

    private sealed record CorpusRow(string Category, string Key, string Title, string Data);
}

internal record CopySrdMonsterRequest(string? Key);

internal record CopySrdMonsterResponse(Guid MonsterId, string SrdKey, string Name, bool Created);

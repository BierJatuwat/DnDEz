using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
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
            string? query, string? type, string? crMax, int? limit, int? offset,
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

            decimal? crCap = null;
            if (!string.IsNullOrWhiteSpace(crMax))
            {
                crCap = ParseChallengeRating(crMax);
                if (crCap is null)
                {
                    return Results.BadRequest(new { error = "invalid_cr" });
                }
            }

            var q = db.SrdMonsters.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(query))
            {
                q = q.Where(m => EF.Functions.ILike(m.Name, "%" + EscapeLike(query.Trim()) + "%"));
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                // ชนิดตรงตัว (ไม่สนตัวพิมพ์) — "Beast" ไม่ควรไปเจอ "Swarm of Tiny Beasts"
                var typeLower = type.Trim().ToLowerInvariant();
                q = q.Where(m => m.Type.ToLower() == typeLower);
            }

            // CR เป็นข้อความ ("1/8") จึงกรอง/เรียงในหน่วยความจำ — ตารางมี 330 แถวและ projection ไม่รวม data (เบา)
            var rows = await q
                .Select(m => new MonsterSummaryRow(m.Key, m.Name, m.Size, m.Type, m.Alignment, m.ArmorClass,
                    m.HitPoints, m.SpeedFeet, m.ReachFeet, m.ChallengeRating, m.Xp))
                .ToListAsync();

            var filtered = rows
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

        app.MapGet("/srd/monsters/{key}", async (string key, ClaimsPrincipal principal, DnDEzDbContext db) =>
        {
            if (principal.GetUserId() is null)
            {
                return Results.Unauthorized();
            }

            var m = await db.SrdMonsters.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key);
            if (m is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                m.Key, m.Name, m.Size, m.Type, m.Alignment, m.ArmorClass, m.HitPoints, m.SpeedFeet, m.ReachFeet,
                m.ChallengeRating, m.Xp, m.SourceVersion,
                data = JsonDocument.Parse(m.Data).RootElement,
                attribution = SrdAttribution.Text,
            });
        }).RequireAuthorization();

        // category: spell | condition | rule — ค้นตามชื่อ (ILIKE) · สำหรับเวทกรองระดับ/คลาสเพิ่มได้ (อ่านจาก data)
        app.MapGet("/srd/corpus", async (
            string? category, string? query, int? level, string? @class, int? limit, int? offset,
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

            var q = db.RulesCorpusEntries.AsNoTracking().AsQueryable();
            if (category is not null)
            {
                q = q.Where(e => e.Category == category);
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                q = q.Where(e => EF.Functions.ILike(e.Title, "%" + EscapeLike(query.Trim()) + "%"));
            }

            // level/class อยู่ใน data (jsonb) ของเวท — กรองในหน่วยความจำหลังดึงมาแค่คอลัมน์เบา ๆ (339 แถว)
            var rows = await q.OrderBy(e => e.Title)
                .Select(e => new CorpusRow(e.Category, e.Key, e.Title, e.Data)).ToListAsync();

            IEnumerable<CorpusRow> filtered = rows;
            if (level is not null || !string.IsNullOrWhiteSpace(@class))
            {
                filtered = rows.Where(r => MatchesSpellFilter(r.Data, level, @class));
            }

            var list = filtered.ToList();
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(offset ?? 0, 0);
            return Results.Ok(new
            {
                total = list.Count,
                items = list.Skip(skip).Take(take).Select(r => new
                {
                    r.Category, r.Key, r.Title, data = JsonDocument.Parse(r.Data).RootElement,
                }),
                attribution = SrdAttribution.Text,
            });
        }).RequireAuthorization();

        app.MapGet("/srd/corpus/{category}/{key}", async (
            string category, string key, ClaimsPrincipal principal, DnDEzDbContext db) =>
        {
            if (principal.GetUserId() is null)
            {
                return Results.Unauthorized();
            }

            var e = await db.RulesCorpusEntries.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Category == category && x.Key == key);
            if (e is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                e.Category, e.Key, e.Title, e.Content, e.License, e.Source,
                data = JsonDocument.Parse(e.Data).RootElement,
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

            if (string.IsNullOrWhiteSpace(request?.Key) || request.Key.Length > 100)
            {
                return Results.BadRequest(new { error = "invalid_key" });
            }

            var srd = await db.SrdMonsters.AsNoTracking().SingleOrDefaultAsync(m => m.Key == request.Key);
            if (srd is null)
            {
                return Results.NotFound();
            }

            var existing = await db.MonsterStatBlocks
                .SingleOrDefaultAsync(m => m.CampaignId == campaignId && m.SrdKey == srd.Key);
            if (existing is not null)
            {
                return Results.Ok(new CopySrdMonsterResponse(existing.Id, srd.Key, existing.Name, Created: false));
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

            return Results.Created(
                $"/campaigns/{campaignId}/monsters/{block.Id}",
                new CopySrdMonsterResponse(block.Id, srd.Key, block.Name, Created: true));
        }).RequireAuthorization();
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

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static bool MatchesSpellFilter(string dataJson, int? level, string? cls)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;
        if (level is not null)
        {
            if (!root.TryGetProperty("level", out var l) || l.ValueKind != JsonValueKind.Number || l.GetInt32() != level)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(cls))
        {
            if (!root.TryGetProperty("classes", out var cs) || cs.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return cs.EnumerateArray().Any(c =>
                string.Equals(c.GetString(), cls.Trim(), StringComparison.OrdinalIgnoreCase));
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

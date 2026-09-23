using System.Text;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Orchestration;

/// <summary>
/// แปลง log ของ session (`messages`) เป็นข้อความก้อนเดียวให้ Claude อ่านตอนสรุป — ใช้ทั้ง recap
/// (`SessionRecapEndpoints`) และบันทึกแคมเปญที่ AI สรุป (`CampaignJournalEndpoints`)
///
/// ⚠️ <paramref name="includeDmPrivate"/> คือเส้นแบ่งเดียวระหว่าง "สรุปให้ผู้เล่นอ่าน" กับ "สรุปให้ DM
/// อ่าน" — ถ้าผลลัพธ์จะไปถึงผู้เล่น (recap ฉบับผู้เล่น) ต้องส่ง false เสมอ ไม่งั้นสิ่งที่ DM ปรึกษา AI
/// ลับ ๆ ผ่าน `/consult` จะถูกสรุปลงไปด้วย (ดู `Message.DmPrivate`)
/// </summary>
internal static class SessionTranscript
{
    /// <summary>
    /// งบรวมของ transcript — BYOK: DM จ่ายค่า token เอง session ยาว ๆ ไม่ควรกลายเป็น request ใหญ่ไม่มี
    /// เพดาน · ราว 60k ตัวอักษร ≈ 15–30k token ขึ้นกับภาษา (ไทยกิน token มากกว่าอังกฤษ) · เกินงบจะตัด
    /// ข้อความเก่าสุดทิ้งก่อน เพราะตอนจบ session สิ่งที่เพิ่งเกิดสำคัญกว่าตอนเปิดฉาก
    /// </summary>
    private const int MaxTotalChars = 60_000;

    /// <summary>ข้อความเดียวที่ยาวผิดปกติ (เช่น AI ตอบยาว ๆ) ไม่ควรกินงบทั้งก้อน</summary>
    private const int MaxCharsPerMessage = 2_000;

    internal static async Task<string> BuildAsync(DnDEzDbContext db, Guid sessionId, bool includeDmPrivate)
    {
        var rows = await (
                from m in db.Messages.AsNoTracking()
                where m.SessionId == sessionId && (includeDmPrivate || !m.DmPrivate)
                join c in db.Characters.AsNoTracking() on m.CharacterId equals c.Id into characters
                from c in characters.DefaultIfEmpty()
                orderby m.CreatedAt descending
                select new { m.AuthorType, m.Content, m.DmPrivate, m.CreatedAt, CharacterName = c == null ? null : c.Name })
            .ToListAsync();

        var entries = rows
            .Select(r => (r.CreatedAt, Speaker: Speaker(r.AuthorType, r.CharacterName, r.DmPrivate), r.Content))
            .ToList();

        // 2026-09-23 — ผู้ช่วย DM ช่องเดียวเก็บบทสนทนาที่ `consult_messages` (ไม่ใช่ `messages` แบบ `/consult` เดิม)
        // ฉบับ DM ต้องรวมที่ DM คุยกับผู้ช่วยระหว่างเซสชันนี้ด้วย ไม่งั้น recap/บันทึกของ DM จะขาดแผนที่ปรึกษาไว้ ·
        // ฉบับผู้เล่นห้ามแตะตารางนี้เด็ดขาด (DM-only ทั้งตาราง)
        if (includeDmPrivate)
        {
            var assistantRows = await db.ConsultMessages.AsNoTracking()
                .Where(m => m.SessionId == sessionId)
                .Select(m => new { m.Role, m.Content, m.CreatedAt })
                .ToListAsync();
            entries.AddRange(assistantRows.Select(r => (
                r.CreatedAt,
                Speaker: r.Role == ConsultRole.Ai ? "DM's assistant (private)" : "DM to assistant (private)",
                r.Content)));
        }

        var lines = new List<string>();
        var total = 0;
        foreach (var row in entries.OrderByDescending(e => e.CreatedAt))
        {
            var content = row.Content.Length > MaxCharsPerMessage
                ? row.Content[..MaxCharsPerMessage] + "…"
                : row.Content;
            var line = $"[{row.Speaker}] {content}";
            if (total + line.Length > MaxTotalChars)
            {
                break;
            }

            lines.Add(line);
            total += line.Length;
        }

        lines.Reverse();
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string Speaker(MessageAuthorType authorType, string? characterName, bool dmPrivate) =>
        authorType switch
        {
            MessageAuthorType.Player => characterName is null ? "Player" : $"Player as {characterName}",
            MessageAuthorType.Ai => dmPrivate ? "AI (private reply to DM)" : "AI narrator",
            MessageAuthorType.Dm => dmPrivate ? "DM (private)" : "DM",
            _ => authorType.ToString(),
        };
}

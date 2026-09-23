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
                select new { m.AuthorType, m.Content, m.DmPrivate, CharacterName = c == null ? null : c.Name })
            .ToListAsync();

        var lines = new List<string>();
        var total = 0;
        foreach (var row in rows)
        {
            var content = row.Content.Length > MaxCharsPerMessage
                ? row.Content[..MaxCharsPerMessage] + "…"
                : row.Content;
            var line = $"[{Speaker(row.AuthorType, row.CharacterName, row.DmPrivate)}] {content}";
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

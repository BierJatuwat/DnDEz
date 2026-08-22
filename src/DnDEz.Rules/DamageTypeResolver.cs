namespace DnDEz.Rules;

/// <summary>
/// คำนวณ damage หลังหัก resistance/vulnerability/immunity ของเป้าหมายต่อ damage type เดียว —
/// แยกไฟล์จาก DamageResolver โดยตั้งใจเพราะเป็น pure math ที่ทดสอบแยกได้อิสระจากการทอยเต๋า
///
/// กฎการซ้อน (D&D 2024 RAW):
/// - Immune ชนะทุกกรณี → 0 เสมอ ไม่สนใจ resist/vulnerable ที่อาจมีพร้อมกัน
/// - มีทั้ง Resistant และ Vulnerable ต่อ type เดียวกัน (และไม่ immune) → หักล้างกันเป็น damage ปกติ
///   (นิยามตรง ๆ ว่า "หักล้างกัน" ไม่ใช่คำนวณ ×0.5×2 เผื่อกรณี rounding ต่างกันจาก floor(floor(x/2)*2))
/// - Resistant อย่างเดียว → หารครึ่ง ปัดลง (floor)
/// - Vulnerable อย่างเดียว → คูณสอง
/// - หลายแหล่งของ resistance/vulnerability ต่อ type เดียวกันไม่ stack ซ้อนกันเอง — รับ input เป็น
///   IReadOnlySet ของเป้าหมายอยู่แล้ว (set = มี/ไม่มี ไม่นับจำนวนแหล่ง) จึงไม่มีทาง stack ได้ตั้งแต่ต้น
/// </summary>
public static class DamageTypeResolver
{
    public static int Resolve(
        DamageType type,
        int rawAmount,
        IReadOnlySet<DamageType> resistances,
        IReadOnlySet<DamageType> vulnerabilities,
        IReadOnlySet<DamageType> immunities)
    {
        if (immunities.Contains(type))
        {
            return 0;
        }

        var isResistant = resistances.Contains(type);
        var isVulnerable = vulnerabilities.Contains(type);

        if (isResistant && isVulnerable)
        {
            return rawAmount;
        }

        if (isResistant)
        {
            return rawAmount / 2;
        }

        if (isVulnerable)
        {
            return rawAmount * 2;
        }

        return rawAmount;
    }
}

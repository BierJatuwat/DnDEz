namespace DnDEz.Rules;

/// <summary>
/// ผลลัพธ์ของ ability check / saving throw เทียบกับ DC — ต่างจาก AttackResult ตรงที่ "ไม่มี"
/// auto-success/auto-fail จาก nat20/nat1 (กฎ D&D 2024/SRD 5.2: check/save ธรรมดาไม่มี critical
/// success/fail อัตโนมัติ ต่างจาก attack roll โดยเฉพาะ) — ดู CheckResolver.Resolve
/// </summary>
public sealed record CheckResult(RollResult Roll, int Dc, bool Success);

/// <summary>
/// เทียบผลทอย d20+modifier (จาก DiceEngine.RollSystem/RollFromDmEntered) กับ DC — ใช้ร่วมกันทั้ง
/// ability check และ saving throw เพราะกลไกเหมือนกันทุกประการ (ต่างแค่บริบทว่าเรียกว่า check
/// หรือ save ซึ่งเป็นเรื่องของชั้นบน ไม่ใช่ engine)
/// </summary>
public static class CheckResolver
{
    /// <summary>success ถ้า Roll.Total &gt;= dc เท่านั้น — ห้ามมี crit logic ปนเข้ามา (นั่นเป็นกฎของ
    /// attack roll เท่านั้น ดู AttackResolver)</summary>
    public static CheckResult Resolve(RollResult roll, int dc)
    {
        var success = roll.Total >= dc;
        return new CheckResult(roll, dc, success);
    }
}

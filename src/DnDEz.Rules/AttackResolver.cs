namespace DnDEz.Rules;

/// <summary>ผลลัพธ์การเทียบ attack roll กับ AC</summary>
public enum AttackOutcome
{
    /// <summary>d20 ดิบออก 20 พอดี — hit อัตโนมัติไม่สนใจ AC เลย</summary>
    CriticalHit,
    Hit,
    Miss,
    /// <summary>d20 ดิบออก 1 พอดี — miss อัตโนมัติไม่สนใจ modifier/AC เลย</summary>
    AutomaticMiss
}

/// <summary>
/// Roll คือผลทอยเต็ม (จาก DiceEngine.RollSystem/RollFromDmEntered) ที่ใช้ประเมินผล —
/// เก็บไว้ทั้งก้อนเพื่อ audit trail เหมือน Phase 1
/// </summary>
public sealed record AttackResult(RollResult Roll, int TargetArmorClass, AttackOutcome Outcome)
{
    public bool IsHit => Outcome is AttackOutcome.CriticalHit or AttackOutcome.Hit;
}

/// <summary>
/// เทียบผล attack roll (d20+attack modifier) กับ AC ของเป้าหมาย — นับจาก "เลขดิบบนลูกเต๋า"
/// ไม่ใช่ total หลังบวก modifier เพราะ total อาจ &gt;= AC ทั้งที่ d20 ไม่ได้ออก 20 จริง (crit ต้องดูเลขดิบ)
/// </summary>
public static class AttackResolver
{
    /// <summary>
    /// หา d20 ดิบที่ถูกเก็บจริง (Sides==20 &amp;&amp; Dropped==false) จาก roll.Rolls แล้วตัดสิน:
    /// ดิบ=20 → CriticalHit เสมอ, ดิบ=1 → AutomaticMiss เสมอ, นอกนั้น hit ถ้า Total&gt;=ac มิฉะนั้น miss
    /// โยน InvalidOperationException ถ้า roll ไม่มี d20 ที่ไม่ถูก drop เลย (แปลว่า expression ไม่ใช่
    /// attack roll ที่ถูกต้อง — attack roll ต้องมี d20 เดี่ยวเสมอตามกติกา D&D)
    /// </summary>
    public static AttackResult Resolve(RollResult roll, int targetArmorClass)
    {
        DieRollDetail? keptD20 = null;
        var keptD20Count = 0;
        foreach (var detail in roll.Rolls)
        {
            if (detail.Sides == 20 && !detail.Dropped)
            {
                keptD20 ??= detail;
                keptD20Count++;
            }
        }

        if (keptD20 is null)
        {
            throw new InvalidOperationException(
                "ไม่พบ d20 ที่ไม่ถูก drop ใน roll นี้ — attack roll ต้องมี d20 เดี่ยวที่ถูกเก็บไว้เสมอ");
        }

        if (keptD20Count > 1)
        {
            throw new InvalidOperationException(
                "พบ d20 ที่ไม่ถูก drop มากกว่า 1 ตัวใน roll นี้ — attack roll ต้องมี d20 เดี่ยวที่ถูกเก็บไว้เสมอ");
        }

        AttackOutcome outcome;
        if (keptD20.Value == 20)
        {
            outcome = AttackOutcome.CriticalHit;
        }
        else if (keptD20.Value == 1)
        {
            outcome = AttackOutcome.AutomaticMiss;
        }
        else
        {
            outcome = roll.Total >= targetArmorClass ? AttackOutcome.Hit : AttackOutcome.Miss;
        }

        return new AttackResult(roll, targetArmorClass, outcome);
    }
}

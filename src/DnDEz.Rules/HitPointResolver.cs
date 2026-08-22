namespace DnDEz.Rules;

/// <summary>
/// ผลลัพธ์การ apply damage หนึ่งครั้ง — รายงานครบทั้ง HP/TempHP ก่อน-หลัง เพื่อ audit
/// (Phase 3 จะรับ IsInstantDeath ไปทำ death-save state machine ต่อ — Phase 2 แค่รายงาน bool)
/// </summary>
public sealed record ApplyDamageResult(
    int PreviousHp,
    int NewHp,
    int PreviousTempHp,
    int NewTempHp,
    int DamageAbsorbedByTempHp,
    int DamageAppliedToHp,
    bool DroppedToZero,
    bool IsInstantDeath);

public sealed record ApplyHealingResult(int PreviousHp, int NewHp, int MaxHp);

/// <summary>
/// จัดการ HP/Temp HP ล้วน ๆ — ไม่รู้จัก Character/CharacterResource entity (engine ไม่รู้จัก
/// concept ผู้ใช้/สิทธิ์/character เลย ตาม PROJECT-CONTEXT §4) รับแค่ตัวเลขดิบ
/// </summary>
public static class HitPointResolver
{
    /// <summary>
    /// Temp HP ถูกหักจาก damage ก่อน HP จริงเสมอ ส่วนที่เหลือหลัง temp HP หมดค่อยไปหัก HP จริง
    /// HP ห้ามติดลบ (clamp ที่ 0) — massive damage instant death: ถ้า HP ลดถึง 0 และ damage ส่วนเกิน
    /// (หลังหัก HP จนถึง 0 แล้ว หัก temp HP ออกไปก่อนแล้ว) &gt;= maxHp → true
    /// </summary>
    public static ApplyDamageResult ApplyDamage(int currentHp, int maxHp, int currentTempHp, int damageAmount)
    {
        var damageAbsorbedByTempHp = Math.Min(damageAmount, currentTempHp);
        var newTempHp = currentTempHp - damageAbsorbedByTempHp;

        var damageAppliedToHp = damageAmount - damageAbsorbedByTempHp;
        var newHp = Math.Max(0, currentHp - damageAppliedToHp);

        var droppedToZero = damageAppliedToHp > 0 && newHp == 0;

        var excess = damageAppliedToHp - currentHp;
        var isInstantDeath = droppedToZero && excess >= maxHp;

        return new ApplyDamageResult(
            PreviousHp: currentHp,
            NewHp: newHp,
            PreviousTempHp: currentTempHp,
            NewTempHp: newTempHp,
            DamageAbsorbedByTempHp: damageAbsorbedByTempHp,
            DamageAppliedToHp: damageAppliedToHp,
            DroppedToZero: droppedToZero,
            IsInstantDeath: isInstantDeath);
    }

    /// <summary>min(current + amount, max)</summary>
    public static ApplyHealingResult ApplyHealing(int currentHp, int maxHp, int healAmount)
    {
        var newHp = Math.Min(currentHp + healAmount, maxHp);
        return new ApplyHealingResult(currentHp, newHp, maxHp);
    }

    /// <summary>
    /// Temp HP ไม่ stack กัน — RAW จริงคือผู้เล่นเลือกเองว่าจะเก็บอันเดิมหรืออันใหม่ แต่ engine ไม่รู้จัก
    /// concept ผู้ใช้/สิทธิ์เลย จึงใช้ "แทนที่ด้วยค่าที่มากกว่า" (Math.Max) เป็น engine-level
    /// simplification ที่สมเหตุสมผลที่สุดของกฎที่จริง ๆ เป็นตัวเลือกของผู้เล่น
    /// </summary>
    public static int SetTempHp(int currentTempHp, int incomingTempHp)
    {
        return Math.Max(currentTempHp, incomingTempHp);
    }
}

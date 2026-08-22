namespace DnDEz.Rules;

/// <summary>
/// สถานะของกระบวนการ death save — ประกาศเองแยกจาก DnDEz.Domain.Enums.DeathSaveStatus (ที่มีแค่
/// Rolling/Stable/Dead) เพราะ Rules ห้าม reference Domain โดยไม่จำเป็น (PROJECT-CONTEXT §4) และ
/// engine ต้องการค่าที่ 4 คือ NotDying เพื่อแทน "สถานะพัก/reset" (ตัวละคร conscious ปกติ หรือเพิ่งฟื้น)
/// แยกจาก Rolling (กำลัง 0 HP และกำลังทอย) อย่างชัดเจน — การแปลง 4 ค่านี้กลับไปเป็น 3 ค่าของ Domain
/// (เช่น NotDying/Stable → "ไม่ตาย") เป็นหน้าที่ชั้น application
/// </summary>
public enum DeathSaveStatus
{
    /// <summary>ไม่ได้กำลัง 0 HP/ทอย death save — สถานะปกติหรือหลังฟื้น/ได้รับการรักษา</summary>
    NotDying,
    /// <summary>อยู่ที่ 0 HP กำลังสะสม success/failure อยู่ ยังไม่ครบ 3 ทั้งสองฝั่ง</summary>
    Rolling,
    /// <summary>สะสม success ครบ 3 — หยุดทอยต่อ (ยังหมดสติอยู่ที่ 0 HP แต่พ้นอันตรายทันที)</summary>
    Stable,
    /// <summary>สะสม failure ครบ 3 — ตาย</summary>
    Dead
}

/// <summary>ผลของการทอย death save หนึ่งครั้ง — Revived คือ nat20 (จบกระบวนการทันที ไม่ใช่แค่ +1 success)</summary>
public enum DeathSaveOutcome
{
    Revived,
    Success,
    Failure,
    /// <summary>nat1 — นับเป็น 2 failures ไม่ใช่ 1</summary>
    CriticalFailure
}

/// <summary>
/// ตัวนับ success/failure ปัจจุบัน + สถานะ — Successes/Failures ถูก clamp ไว้ที่ 3 เสมอ
/// (ป้องกัน nat1 ที่ +2 ตอนอยู่ 2 อยู่แล้วทำให้เกิน 3 แบบไม่มีความหมาย)
/// </summary>
public sealed record DeathSaveState(int Successes, int Failures, DeathSaveStatus Status)
{
    /// <summary>สถานะ reset/พัก — ใช้ตอนตัวละคร conscious ปกติ (ไม่ได้กำลัง 0 HP) หรือหลังฟื้น/heal
    /// ระหว่างกำลังทอย death save (caller เรียกค่านี้แทนการคำนวณเอง — ดู PROJECT-CONTEXT บรีฟ Phase 3)</summary>
    public static DeathSaveState NotDying { get; } = new(0, 0, DeathSaveStatus.NotDying);

    /// <summary>สถานะเริ่มต้นตอนตัวละครเพิ่งตกลงไป 0 HP (DroppedToZero=true, IsInstantDeath=false
    /// จาก HitPointResolver.ApplyDamage) — พร้อมเริ่มทอย death save ตอนขึ้นเทิร์นตัวเองครั้งถัดไป</summary>
    public static DeathSaveState StartRolling { get; } = new(0, 0, DeathSaveStatus.Rolling);
}

/// <summary>ผลลัพธ์การทอย death save ครั้งหนึ่ง — RevivedWithOneHp=true หมายความว่า caller ต้องไปเรียก
/// HitPointResolver.ApplyHealing(currentHp: 0, maxHp, healAmount: 1) ต่อเอง (Phase 3 ไม่คำนวณ HP เอง)</summary>
public sealed record DeathSaveRollResult(
    RollResult Roll,
    DeathSaveOutcome Outcome,
    DeathSaveState PreviousState,
    DeathSaveState NewState,
    bool RevivedWithOneHp);

/// <summary>ผลลัพธ์ของเหตุการณ์ "โดนโจมตีตอนอยู่ที่ 0 HP" — ไม่มี RollResult เพราะไม่ใช่การทอยลูกเต๋า
/// เป็นการนับ failure อัตโนมัติ (1 ปกติ, 2 ถ้า critical hit)</summary>
public sealed record DeathSaveAutoFailureResult(
    DeathSaveState PreviousState,
    DeathSaveState NewState,
    bool WasCriticalHit,
    int FailuresApplied);

/// <summary>
/// Death save state machine (SRD 5.2) — ใช้ต่อจาก HitPointResolver.ApplyDamage ตอน DroppedToZero=true
/// และ IsInstantDeath=false เท่านั้น (ถ้า IsInstantDeath=true ตายทันทีไม่ผ่าน death save เลย —
/// resolver นี้ไม่มี logic สำหรับ massive damage ซ้ำตามที่บรีฟกำหนด)
/// </summary>
public static class DeathSaveResolver
{
    /// <summary>
    /// ประเมินผลทอย death save หนึ่งครั้ง (roll ต้องมาจาก DiceEngine.RollSystem("1d20", ...) —
    /// รองรับ advantage/disadvantage ผ่าน roll.Advantage ตามปกติ แต่ advantage/disadvantage มีผลแค่
    /// การเลือกลูกที่เก็บเท่านั้น ไม่มีผลต่อกติกานับ success/fail ของ death save เอง) —
    /// นับจาก "เลขดิบบน d20 ที่ถูกเก็บจริง" เหมือนแพทเทิร์นของ AttackResolver: หา entry เดียวที่
    /// Sides==20 &amp;&amp; Dropped==false โยน InvalidOperationException ถ้าไม่มีเลยหรือมีเกิน 1 ตัว
    /// </summary>
    public static DeathSaveRollResult Resolve(RollResult roll, DeathSaveState currentState)
    {
        if (currentState.Status != DeathSaveStatus.Rolling)
        {
            throw new InvalidOperationException(
                $"ทอย death save ได้เฉพาะตอนสถานะเป็น Rolling เท่านั้น (สถานะปัจจุบัน: {currentState.Status})");
        }

        var keptValue = FindKeptD20Value(roll);

        if (keptValue == 20)
        {
            return new DeathSaveRollResult(
                roll, DeathSaveOutcome.Revived, currentState, DeathSaveState.NotDying, RevivedWithOneHp: true);
        }

        if (keptValue == 1)
        {
            var newState = AddFailures(currentState, 2);
            return new DeathSaveRollResult(
                roll, DeathSaveOutcome.CriticalFailure, currentState, newState, RevivedWithOneHp: false);
        }

        if (keptValue >= 10)
        {
            var newState = AddSuccesses(currentState, 1);
            return new DeathSaveRollResult(
                roll, DeathSaveOutcome.Success, currentState, newState, RevivedWithOneHp: false);
        }

        // เหลือแค่ช่วง 2-9
        var failedState = AddFailures(currentState, 1);
        return new DeathSaveRollResult(
            roll, DeathSaveOutcome.Failure, currentState, failedState, RevivedWithOneHp: false);
    }

    /// <summary>
    /// โดนโจมตีตอนอยู่ที่ 0 HP (เหตุการณ์แยกจากการทอย death save ตอนขึ้นเทิร์นตัวเอง) — นับเป็น
    /// 1 failure อัตโนมัติทันที หรือ 2 failures ถ้าเป็น critical hit ไม่ต้องทอยลูกเต๋าใด ๆ
    /// </summary>
    public static DeathSaveAutoFailureResult ApplyAttackedAtZeroHp(DeathSaveState currentState, bool isCriticalHit)
    {
        if (currentState.Status != DeathSaveStatus.Rolling)
        {
            throw new InvalidOperationException(
                $"รับ auto-failure จากการโดนโจมตีได้เฉพาะตอนสถานะเป็น Rolling เท่านั้น " +
                $"(สถานะปัจจุบัน: {currentState.Status})");
        }

        var failuresApplied = isCriticalHit ? 2 : 1;
        var newState = AddFailures(currentState, failuresApplied);

        return new DeathSaveAutoFailureResult(currentState, newState, isCriticalHit, failuresApplied);
    }

    private static DeathSaveState AddSuccesses(DeathSaveState state, int count)
    {
        var successes = Math.Min(3, state.Successes + count);
        var status = successes >= 3 ? DeathSaveStatus.Stable : DeathSaveStatus.Rolling;
        return new DeathSaveState(successes, state.Failures, status);
    }

    private static DeathSaveState AddFailures(DeathSaveState state, int count)
    {
        var failures = Math.Min(3, state.Failures + count);
        var status = failures >= 3 ? DeathSaveStatus.Dead : DeathSaveStatus.Rolling;
        return new DeathSaveState(state.Successes, failures, status);
    }

    /// <summary>หา d20 ดิบที่ไม่ถูก drop ตัวเดียวใน roll — เหมือนแพทเทิร์นใน AttackResolver.Resolve
    /// ทุกประการ (บทเรียนจาก Phase 2 code review: ต้องเช็คทั้งกรณีไม่มีเลยและมีเกิน 1 ตัว)</summary>
    private static int FindKeptD20Value(RollResult roll)
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
                "ไม่พบ d20 ที่ไม่ถูก drop ใน roll นี้ — death save ต้องมี d20 เดี่ยวที่ถูกเก็บไว้เสมอ");
        }

        if (keptD20Count > 1)
        {
            throw new InvalidOperationException(
                "พบ d20 ที่ไม่ถูก drop มากกว่า 1 ตัวใน roll นี้ — death save ต้องมี d20 เดี่ยวที่ถูกเก็บไว้เสมอ");
        }

        return keptD20.Value;
    }
}

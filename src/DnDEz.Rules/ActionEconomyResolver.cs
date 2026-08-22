namespace DnDEz.Rules;

/// <summary>
/// ทรัพยากรของหนึ่งเทิร์น (player-side-design §4): action/bonus action ใช้ได้ 1 ครั้งต่อเทิร์น,
/// reaction ใช้ได้ 1 ครั้งแต่ reset เฉพาะตอนขึ้นเทิร์นตัวเอง (ไม่มี method reset ระหว่างเทิร์นคนอื่น
/// โดยตั้งใจ — ต้องรอ StartTurn ของเจ้าของเองรอบหน้า), movement ลดทีละส่วนได้ไม่ใช่ all-or-nothing
/// — เป็น record ล้วน ไม่รู้จัก Character/turn order/timer ใด ๆ (engine ไม่รู้จัก concept ผู้ใช้เลย)
/// </summary>
public sealed record ActionEconomyState(
    bool ActionAvailable,
    bool BonusActionAvailable,
    bool ReactionAvailable,
    int MovementRemainingFeet,
    int MaxMovementFeet);

/// <summary>
/// Resource tracking ล้วน ๆ ไม่มี RNG เกี่ยวข้องเลย (S5) — double-spend (ใช้ action/bonus/reaction
/// ซ้ำ หรือใช้ movement เกินคงเหลือ) ถือเป็น caller bug ต้อง throw เหมือนแพทเทิร์นอื่นในระบบ
/// (เช่น AttackResolver โยนเมื่อ roll shape ผิด) ไม่ใช่ game outcome ที่ engine ต้องดูดซับเงียบ ๆ —
/// **ห้ามมี timer/async wait ในนี้เด็ดขาด** reaction timeout เป็นหน้าที่ชั้น application/background
/// job (Hangfire) engine แค่ track available/used เป็น bool synchronous ล้วน
/// </summary>
public static class ActionEconomyResolver
{
    /// <summary>
    /// เริ่มเทิร์นใหม่ — action/bonus action/reaction กลับมา available ทั้งหมด, movement เต็ม
    /// รับ maxMovementFeet ใหม่ทุกครั้งเพราะ speed อาจเปลี่ยนจาก effect ระหว่างเกม (เช่น slow/haste/
    /// grapple ทำ speed=0) — ไม่ cache ค่าจากเทิร์นก่อนหน้า
    /// </summary>
    public static ActionEconomyState StartTurn(int maxMovementFeet)
    {
        if (maxMovementFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMovementFeet), maxMovementFeet, "maxMovementFeet ห้ามติดลบ");
        }

        return new ActionEconomyState(
            ActionAvailable: true,
            BonusActionAvailable: true,
            ReactionAvailable: true,
            MovementRemainingFeet: maxMovementFeet,
            MaxMovementFeet: maxMovementFeet);
    }

    /// <summary>ใช้ action — throw ถ้าใช้ไปแล้วในเทิร์นนี้ (caller/UI ควรรู้จาก state ก่อนเสนอปุ่มอยู่แล้ว)</summary>
    public static ActionEconomyState SpendAction(ActionEconomyState state)
    {
        if (!state.ActionAvailable)
        {
            throw new InvalidOperationException("Action ถูกใช้ไปแล้วในเทิร์นนี้");
        }

        return state with { ActionAvailable = false };
    }

    /// <summary>ใช้ bonus action — throw ถ้าใช้ไปแล้วในเทิร์นนี้</summary>
    public static ActionEconomyState SpendBonusAction(ActionEconomyState state)
    {
        if (!state.BonusActionAvailable)
        {
            throw new InvalidOperationException("Bonus action ถูกใช้ไปแล้วในเทิร์นนี้");
        }

        return state with { BonusActionAvailable = false };
    }

    /// <summary>ใช้ reaction — throw ถ้าใช้ไปแล้ว ไม่มี method ใดใน resolver นี้ที่ reset reaction
    /// นอกจาก StartTurn ตามกติกาที่ reaction reset เฉพาะตอนขึ้นเทิร์นตัวเองเท่านั้น</summary>
    public static ActionEconomyState SpendReaction(ActionEconomyState state)
    {
        if (!state.ReactionAvailable)
        {
            throw new InvalidOperationException("Reaction ถูกใช้ไปแล้ว — ต้องรอถึงเทิร์นตัวเองรอบหน้า");
        }

        return state with { ReactionAvailable = false };
    }

    /// <summary>ใช้ movement บางส่วน — หักออกจากคงเหลือ throw ถ้า feet ติดลบ หรือถ้าใช้เกินคงเหลือ</summary>
    public static ActionEconomyState SpendMovement(ActionEconomyState state, int feet)
    {
        if (feet < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(feet), feet, "ระยะที่ใช้ห้ามติดลบ");
        }

        if (feet > state.MovementRemainingFeet)
        {
            throw new InvalidOperationException(
                $"ใช้ movement เกินที่มี (เหลือ {state.MovementRemainingFeet} ฟุต ขอใช้ {feet} ฟุต)");
        }

        return state with { MovementRemainingFeet = state.MovementRemainingFeet - feet };
    }
}

namespace DnDEz.Rules;

/// <summary>
/// ราคาที่ต้องจ่ายเป็นทรัพยากรของเทิร์นเพื่อใช้ท่านั้น (player-side-design §4 — ชุดเดียวกับ
/// <see cref="ActionEconomyState"/>) · <see cref="None"/> มีไว้สำหรับท่าที่ไม่กินทรัพยากรเลย
/// (เช่น free interaction) ซึ่งต้องเสนอได้เสมอแม้ใช้ action/bonus/reaction หมดแล้ว
/// </summary>
public enum ActionResourceCost
{
    /// <summary>กิน action ของเทิร์น</summary>
    Action,

    /// <summary>กิน bonus action ของเทิร์น</summary>
    BonusAction,

    /// <summary>กิน reaction (reset เฉพาะตอนขึ้นเทิร์นตัวเองรอบหน้า)</summary>
    Reaction,

    /// <summary>ไม่กินทรัพยากรใดเลย — ไม่เคยถูกตัดออกด้วยด่านทรัพยากร</summary>
    None
}

/// <summary>
/// รหัสของเหตุผลที่ทำให้ท่า "ทำได้แต่มีราคา" (ระดับ 2 ของ §1.4) — มีไว้ให้เทสต์และชั้น API อ้างอิงได้
/// แบบไม่ต้องจับคู่สตริง ส่วนข้อความที่ผู้เล่นอ่านอยู่ใน <see cref="AffordanceNote.Message"/>
///
/// **จงใจไม่มีรหัสสำหรับ "ทำไม่ได้"** เพราะระดับ 1 ของ §1.4 คือหายไปจากรายการเลย ไม่ใช่ส่งไปพร้อม
/// เหตุผลแล้วให้ client ซ่อนเอง — ถ้าวันหนึ่งมีคนเพิ่มรหัสแนว "OutOfRange" เข้ามาที่นี่ แปลว่าโมเดล
/// สามระดับกำลังถูกทำให้พังโดยไม่ตั้งใจ
/// </summary>
public enum AffordanceNoteCode
{
    /// <summary>เป้าอยู่เลยระยะปกติ แต่ยังไม่เลยระยะไกลสุด → ทอยเสียเปรียบ</summary>
    BeyondNormalRange,

    /// <summary>ทอยโจมตีระยะไกลขณะมีศัตรูประชิดตัวผู้กระทำ → ทอยเสียเปรียบ</summary>
    RangedAttackWithEnemyAdjacent
}

/// <summary>
/// ท่าหนึ่งท่าที่ "อาจ" ทำได้ ย่อยมาจนเหลือเฉพาะสิ่งที่กฎทั่วไปต้องใช้ (§2.2: กฎอยู่ใน engine
/// ไม่ใช่ใน catalog) — ทุกฟิลด์ที่นี่เป็นข้อมูลที่ catalog มีอยู่แล้วทุกแถว ไม่ว่าจะเป็นอาวุธ เวท
/// หรือท่าของมอนสเตอร์ resolver จึงครอบกฎเดียวใช้ได้หมดโดยไม่ต้องมีเงื่อนไขรายตัวสักแถว
/// </summary>
/// <param name="Id">รหัสของแถวใน catalog — ติดกลับไปกับผลลัพธ์เพื่อให้ client ส่งกลับมาสั่งใช้ได้</param>
/// <param name="Name">ชื่อที่แสดงบนปุ่ม</param>
/// <param name="Cost">ทรัพยากรที่ต้องจ่าย</param>
/// <param name="RequiresAttackRoll">
/// ต้องทอยโจมตีไหม — **ตัวชี้ขาดของกฎศัตรูประชิด** ไม่ใช่ "เป็นเวทหรือเปล่า" (ดู
/// <see cref="AffordanceResolver.Resolve"/>)
/// </param>
/// <param name="TargetsEnemy">true = เล็งศัตรู, false = เล็งพวกเดียวกัน (เช่นเวทรักษา)</param>
/// <param name="NormalRangeFeet">
/// ระยะปกติเป็นฟุต · อาวุธประชิดใช้ระยะเอื้อม (5 ฟุตสำหรับอาวุธทั่วไป) · null = ท่าที่ไม่ผูกกับระยะ
/// </param>
/// <param name="LongRangeFeet">
/// ระยะไกลสุดเป็นฟุต · null = ท่านี้มีระยะเดียว ไม่มีโซนเสียเปรียบ (เลยระยะปกติคือทำไม่ได้เลย)
/// </param>
public sealed record AffordanceOption(
    Guid Id,
    string Name,
    ActionResourceCost Cost,
    bool RequiresAttackRoll,
    bool TargetsEnemy,
    int? NormalRangeFeet,
    int? LongRangeFeet);

/// <summary>
/// เป้าหนึ่งเป้าที่ **ผู้ดูคนนั้นมองเห็นแล้ว** — ชั้น API เป็นคนกรองหมอกก่อนส่งเข้ามา (§1.2)
///
/// ระยะเป็นฟุตที่คำนวณมาแล้วจากตัวผู้กระทำ ไม่ใช่พิกัดบนกริด เพราะ resolver นี้ไม่ควรรู้ตำแหน่งจริง
/// ของใครเลย — รู้แค่ "ห่างเท่าไร" ก็พอตัดสินกฎระยะได้ครบ และเป็นข้อมูลที่รั่วออกไปได้น้อยที่สุด
/// </summary>
/// <param name="Id">รหัสของผู้ร่วมฉาก</param>
/// <param name="Name">ชื่อที่ผู้ดูคนนั้นมีสิทธิ์เห็น (ติดไปกับผลลัพธ์ ไม่ให้ client ไปหาเอง)</param>
/// <param name="IsEnemy">เป็นศัตรูของผู้กระทำไหม (ใช้ทั้งจับคู่เป้า และเช็คว่ามีใครประชิดตัวอยู่)</param>
/// <param name="DistanceFeet">ระยะจากผู้กระทำถึงเป้า หน่วยเป็นฟุต</param>
public sealed record AffordanceTarget(
    Guid Id,
    string Name,
    bool IsEnemy,
    int DistanceFeet);

/// <summary>
/// ป้ายบอกราคาของทางเลือกหนึ่งข้อ — <see cref="Message"/> ต้องอ่านรู้เรื่องในตัวมันเองเป็นภาษาคน
/// (§1.4: ผู้เล่นใหม่เรียนกฎจากการเห็นป้ายพวกนี้ซ้ำ ๆ) จึง **ห้ามส่งเป็นรหัสให้ client ไปแปลเอง**
/// ส่วน <see cref="Code"/> มีไว้ให้โค้ดอื่นเทียบแบบไม่ต้องจับคู่สตริง
/// </summary>
/// <param name="Code">รหัสของกฎที่ทำให้เกิดราคานี้</param>
/// <param name="Message">ข้อความภาษาไทยที่อธิบายราคาให้คนที่ไม่เคยอ่านหนังสือกฎเข้าใจ</param>
public sealed record AffordanceNote(AffordanceNoteCode Code, string Message);

/// <summary>
/// หนึ่งบรรทัดในพาเนล "ตาฉันแล้ว ทำอะไรได้บ้าง" = คู่ (ท่า, เป้า) ที่ทำได้จริง พร้อมราคาทั้งหมด
///
/// **การมีอยู่ของ record นี้แปลว่า "ทำได้"** — ทุกอย่างที่ทำไม่ได้ถูกตัดทิ้งไปตั้งแต่ใน resolver แล้ว
/// (§1.1: *"ศัตรูที่อยู่ไกลเกินไม่ปรากฏในลิสต์ตั้งแต่แรก"*) client จึงไม่ต้องมีตรรกะซ่อนปุ่มเองเลย
/// และไม่มีทางแสดงปุ่มที่กดแล้วเด้ง error
/// </summary>
/// <param name="OptionId">รหัสท่า</param>
/// <param name="OptionName">ชื่อท่า</param>
/// <param name="TargetId">รหัสเป้า (null สำหรับท่าที่ไม่เล็งใคร)</param>
/// <param name="TargetName">ชื่อเป้า (null สำหรับท่าที่ไม่เล็งใคร)</param>
/// <param name="AdvantageMode">ผลรวมได้เปรียบ/เสียเปรียบ ที่ผ่าน <see cref="AdvantageResolver"/> มาแล้ว</param>
/// <param name="Notes">เหตุผลของราคาทั้งหมด — ว่างเปล่า = ทำได้ปกติ (ระดับ 3)</param>
public sealed record Affordance(
    Guid OptionId,
    string OptionName,
    Guid? TargetId,
    string? TargetName,
    AdvantageMode AdvantageMode,
    IReadOnlyList<AffordanceNote> Notes);

/// <summary>
/// คำนวณว่า "ตานี้ทำอะไรได้บ้าง" แบบ deterministic ล้วน (spatial-play-design §1.3: เรื่องนี้
/// **ไม่ใช่งานของ AI** เพราะมีคำตอบตายตัวและเขียนเทสต์ได้ อยู่หมวดเดียวกับ
/// <see cref="ActionEconomyResolver"/> และ <see cref="MovementCostResolver"/>) — ไม่มี RNG ไม่มี I/O
/// ไม่รับ <see cref="IDiceRoller"/> คำตอบขึ้นกับอาร์กิวเมนต์สามตัวที่รับเข้ามาเท่านั้น
///
/// โมเดล 3 ระดับของ §1.4 ที่คลาสนี้บังคับใช้:
/// <list type="number">
///   <item>ทำไม่ได้ → **ไม่อยู่ในรายการเลย** (ทรัพยากรหมด / เป้าผิดชนิด / เลยระยะไกลสุด)</item>
///   <item>ทำได้แต่มีราคา → อยู่ในรายการ พร้อม <see cref="AffordanceNote"/> เป็นภาษาคน</item>
///   <item>ทำได้ปกติ → อยู่ในรายการเฉย ๆ <see cref="Affordance.Notes"/> ว่าง</item>
/// </list>
///
/// **สองกฎเสียเปรียบที่ใช้ อ้าง SRD 5.2 ฉบับ CC:**
/// <list type="bullet">
///   <item>*"Your attack roll has Disadvantage when your target is beyond normal range, and you
///     can't attack a target beyond long range."*</item>
///   <item>*"When you make a ranged attack roll with a weapon, a spell, or some other means, you
///     have Disadvantage on the roll if you are within 5 feet of an enemy who can see you and
///     doesn't have the Incapacitated condition."*</item>
/// </list>
///
/// **ความปลอดภัย (§1.2) เป็นเรื่องของ "รับอะไรเข้ามา" ไม่ใช่ "กรองอะไรออกไป"** — resolver รับเฉพาะ
/// เป้าที่ผู้ดูคนนั้นมองเห็นแล้ว จึงไม่มีทางพูดถึงสิ่งที่มองไม่เห็นได้เลยเชิงโครงสร้าง ไม่ใช่คำนวณจาก
/// ความจริงทั้งใบแล้วค่อยกรองทีหลัง · **ห้ามเพิ่มพารามิเตอร์หรือการไปดึงข้อมูลเองเด็ดขาด** (DbContext,
/// สถานะโลกทั้งใบ, ตำแหน่งจริงบนกริด) เพราะนั่นจะทำลายคุณสมบัตินี้ทันทีโดยที่เทสต์ยังเขียวอยู่
/// </summary>
public static class AffordanceResolver
{
    /// <summary>
    /// ระยะ "ประชิด" มาตรฐาน 5 ฟุต — ใช้สองที่: เกณฑ์ว่าท่าไหนนับเป็น "ระยะไกล" และรัศมีที่ถือว่ามี
    /// ศัตรูประชิดตัวผู้กระทำ · เลข 5 มาจากตัวบทกฎตรง ๆ (*"within 5 feet of an enemy"*) ไม่ใช่ขนาดช่อง
    /// บนกริด — บังเอิญเท่ากับ <see cref="MovementCostResolver.FeetPerSquare"/> แต่คนละเรื่องกัน
    /// จึงไม่อ้างอิงค่าข้ามกัน
    /// </summary>
    public const int MeleeReachFeet = 5;

    /// <summary>
    /// แตกท่าทั้งหมด × เป้าที่มองเห็นทั้งหมด ให้เหลือเฉพาะคู่ที่ทำได้จริง พร้อมราคาของแต่ละคู่
    ///
    /// พาเนลเป็น **ผลลัพธ์ที่คำนวณมาแล้ว ไม่ใช่ฟอร์มให้เลือกเป้าทีหลัง** (§1.1) ท่าเดียวกับศัตรู 2 ตัว
    /// จึงได้ 2 บรรทัดแยกกัน เพราะราคาของแต่ละเป้าไม่จำเป็นต้องเท่ากัน (เป้าไกลกว่าอาจติดเสียเปรียบ
    /// ขณะที่เป้าใกล้ไม่ติด) — ถ้ายุบเป็นบรรทัดเดียวแล้วให้เลือกเป้าทีหลัง ผู้เล่นจะเห็นราคาหลังกดไปแล้ว
    /// </summary>
    /// <param name="economy">ทรัพยากรคงเหลือของเทิร์นนี้ — ใช้ตัดท่าที่จ่ายไม่ไหวออกทั้งท่า</param>
    /// <param name="options">ท่าที่ผู้กระทำมีอยู่ (ย่อยมาจาก catalog แล้ว)</param>
    /// <param name="visibleTargets">เป้าที่ผู้ดูคนนั้นมองเห็นแล้วเท่านั้น</param>
    /// <returns>ทุกคู่ (ท่า, เป้า) ที่ทำได้ — ลำดับตาม options แล้วตาม visibleTargets แบบ deterministic</returns>
    public static IReadOnlyList<Affordance> Resolve(
        ActionEconomyState economy,
        IReadOnlyList<AffordanceOption> options,
        IReadOnlyList<AffordanceTarget> visibleTargets)
    {
        // เช็คครั้งเดียวนอกลูป: กฎประชิดถามว่า "มีศัตรูอยู่ติดตัว **ผู้กระทำ** ไหม" ไม่ได้ถามว่ายิงใคร
        // ค่านี้จึงเหมือนกันหมดทุกคู่ในรอบนี้ · ผลพลอยได้คือศัตรูที่ยืนประชิดเองก็ยังเป็นเป้าที่ยิงได้
        // ตามปกติ และการยิงมันก็ติดเสียเปรียบเหมือนยิงเป้าไกล ๆ
        var enemyWithinReach = false;
        foreach (var target in visibleTargets)
        {
            if (target.IsEnemy && target.DistanceFeet <= MeleeReachFeet)
            {
                enemyWithinReach = true;
                break;
            }
        }

        var results = new List<Affordance>();

        foreach (var option in options)
        {
            if (!CanAfford(economy, option.Cost))
            {
                continue;
            }

            foreach (var target in visibleTargets)
            {
                // ท่าเล็งศัตรูจับคู่กับศัตรูเท่านั้น ท่าเล็งพวกเดียวกันจับคู่กับพวกเดียวกันเท่านั้น
                // (catalog มีเป้าชนิด Ally อยู่ด้วย เช่นเวทรักษา จึงเทียบสองทางไม่ใช่เช็คแค่ฝั่งศัตรู)
                if (option.TargetsEnemy != target.IsEnemy)
                {
                    continue;
                }

                if (!IsWithinMaximumRange(option, target.DistanceFeet))
                {
                    continue;
                }

                var notes = new List<AffordanceNote>();

                if (IsBeyondNormalRange(option, target.DistanceFeet))
                {
                    notes.Add(new AffordanceNote(
                        AffordanceNoteCode.BeyondNormalRange,
                        "ไกลเกินระยะที่ยิงถนัด — ทอยโจมตีแบบเสียเปรียบ (ทอย 2 ลูก เอาลูกที่แย่กว่า)"));
                }

                if (HasRangedInCloseCombatPenalty(option, enemyWithinReach))
                {
                    notes.Add(new AffordanceNote(
                        AffordanceNoteCode.RangedAttackWithEnemyAdjacent,
                        "มีศัตรูประชิดตัวอยู่ — โจมตีระยะไกลตอนนี้ต้องทอยแบบเสียเปรียบ "
                        + "(ทอย 2 ลูก เอาลูกที่แย่กว่า)"));
                }

                // ต้องผ่าน AdvantageResolver เสมอ ห้ามสรุปเองว่า "มีโน้ต = เสียเปรียบ" — ตอนนี้ยัง
                // ไม่มีแหล่งได้เปรียบในระบบ (ผู้โจมตีที่ซ่อนอยู่, เป้าที่ล้มอยู่, การช่วยเหลือ ยังไม่ได้
                // ทำโมเดล) จึงส่ง hasAdvantage: false ไปตรง ๆ · การเดินผ่านตัวรวมกลางตั้งแต่วันนี้
                // แปลว่าเมื่อแหล่งได้เปรียบมาถึงจริง กฎหักล้างของ D&D 2024 (ได้เปรียบ + เสียเปรียบ
                // = ทอยปกติ ไม่ทบชั้น) จะถูกต้องอยู่แล้วในที่เดียว ไม่ต้องไล่แก้ทุก resolver
                var advantageMode = AdvantageResolver.Resolve(
                    hasAdvantage: false,
                    hasDisadvantage: notes.Count > 0);

                results.Add(new Affordance(
                    option.Id,
                    option.Name,
                    target.Id,
                    target.Name,
                    advantageMode,
                    notes));
            }
        }

        return results;
    }

    /// <summary>
    /// ด่านทรัพยากร (§2.3 "ตัดตัวเลือกที่ทรัพยากรหมด") — ไม่ผ่านคือท่านั้นหายไปทั้งท่า ทุกเป้า
    /// ผู้เล่นจึงไม่ต้องเดาว่าทำไมกดไม่ได้ เพราะมันไม่อยู่ให้กดตั้งแต่แรก
    /// </summary>
    private static bool CanAfford(ActionEconomyState economy, ActionResourceCost cost) => cost switch
    {
        ActionResourceCost.Action => economy.ActionAvailable,
        ActionResourceCost.BonusAction => economy.BonusActionAvailable,
        ActionResourceCost.Reaction => economy.ReactionAvailable,
        ActionResourceCost.None => true,
        _ => throw new ArgumentOutOfRangeException(nameof(cost), cost, "ราคาทรัพยากรที่ไม่รู้จัก")
    };

    /// <summary>
    /// SRD 5.2: *"you can't attack a target beyond long range."* → เลยระยะไกลสุดคือ **ทำไม่ได้จริง**
    /// (ระดับ 1 หายไปเลย) ไม่ใช่ทำได้แบบมีราคา
    ///
    /// ระยะไกลสุด = <see cref="AffordanceOption.LongRangeFeet"/> ถ้ามี ไม่งั้นใช้
    /// <see cref="AffordanceOption.NormalRangeFeet"/> — ท่าที่มีระยะเดียวคือเลยระยะนั้นแล้วจบ
    /// **ห้ามเดาว่ามีระยะไกลสุดซ่อนอยู่** (เช่นคูณสองให้เอง) เพราะเวทอย่าง Fire Bolt ไม่มีโซนเสียเปรียบ
    /// ในกฎเลย ถ้าเดาให้ ผู้เล่นจะยิงได้ไกลกว่าที่กฎอนุญาต
    ///
    /// ไม่ระบุระยะทั้งสองค่า = ท่าที่ไม่ผูกกับระยะ (เช่น free interaction) ไม่มีด่านนี้มากั้น
    /// </summary>
    private static bool IsWithinMaximumRange(AffordanceOption option, int distanceFeet)
    {
        var maximumFeet = option.LongRangeFeet ?? option.NormalRangeFeet;
        return maximumFeet is null || distanceFeet <= maximumFeet.Value;
    }

    /// <summary>
    /// SRD 5.2: *"Your attack roll has Disadvantage when your target is beyond normal range"* —
    /// โซนระหว่างระยะปกติกับระยะไกลสุดคือระดับ 2 (ทำได้ แต่มีราคา)
    ///
    /// ต้องมี <see cref="AffordanceOption.LongRangeFeet"/> จริงถึงจะมีโซนนี้ได้ ท่าที่มีระยะเดียว
    /// ไม่มีทางเข้าเงื่อนไขนี้เลยเพราะเลยระยะปกติก็ถูกตัดทิ้งไปแล้วที่
    /// <see cref="IsWithinMaximumRange"/>
    /// </summary>
    private static bool IsBeyondNormalRange(AffordanceOption option, int distanceFeet) =>
        option is { NormalRangeFeet: { } normal, LongRangeFeet: { } longRange }
        && distanceFeet > normal
        && distanceFeet <= longRange;

    /// <summary>
    /// SRD 5.2: *"When you make a ranged attack roll with a weapon, a spell, or some other means,
    /// you have Disadvantage on the roll if you are within 5 feet of an enemy who can see you and
    /// doesn't have the Incapacitated condition."*
    ///
    /// **กฎผูกกับ "การทอยโจมตีระยะไกล" ไม่ใช่ "เวท"** — Magic Missile ที่ยิงโดนอัตโนมัติไม่มีทอยโจมตี
    /// จึงไม่ติดแม้ศัตรูประชิด · ถ้าเขียนเป็น "เวทระยะไกล + ประชิด = เสียเปรียบ" ตามคำบรรยายลอย ๆ
    /// ผู้เล่นจะโดนโทษที่กฎไม่ได้กำหนด · ส่วนอาวุธประชิดก็ไม่ติดเพราะระยะเอื้อมไม่เกิน
    /// <see cref="MeleeReachFeet"/> การรบประชิดตอนศัตรูประชิดคือเรื่องปกติของมัน
    ///
    /// เงื่อนไข "who can see you and doesn't have the Incapacitated condition" **ยังไม่ได้ทำโมเดล**
    /// เพราะ <see cref="AffordanceTarget"/> ยังไม่มีสภาวะ/การมองเห็นของฝั่งศัตรู — ผลคือตอนนี้กฎนี้
    /// เข้มกว่าความจริงเล็กน้อย (ศัตรูที่หมดสภาพหรือมองไม่เห็นเราก็ยังทำให้ติดเสียเปรียบ) ซึ่งเป็นทาง
    /// ที่ปลอดภัยกว่าการปล่อยผ่าน และต้องแก้เมื่อโมเดลสภาวะเข้ามาถึง
    /// </summary>
    private static bool HasRangedInCloseCombatPenalty(AffordanceOption option, bool enemyWithinReach) =>
        option.RequiresAttackRoll
        && enemyWithinReach
        && option.NormalRangeFeet > MeleeReachFeet;
}

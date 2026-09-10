namespace DnDEz.Rules.Tests;

/// <summary>
/// "ตาฉันแล้ว ทำอะไรได้บ้าง" — คำนวณแบบ deterministic ล้วน (spatial-play-design §1.3: เรื่องนี้
/// **ไม่ใช่งานของ AI** เพราะมีคำตอบตายตัวและเขียนเทสต์ได้ อยู่หมวดเดียวกับ ActionEconomyResolver)
///
/// โมเดล 3 ระดับของ §1.4:
///   1. ทำไม่ได้ → **ไม่อยู่ในรายการเลย** (ไม่ใช่ส่งไปแล้วให้ client ซ่อน)
///   2. ทำได้แต่มีราคา → อยู่ในรายการ **พร้อมเหตุผลเป็นข้อความภาษาคน**
///   3. ทำได้ปกติ → อยู่เฉย ๆ
///
/// **กฎทั่วไปอยู่ใน engine ไม่ใช่ใน catalog** (§2.2) — resolver นี้รู้จักแค่ "ระยะเท่าไร ต้องทอย
/// โจมตีไหม ราคาเท่าไร" ซึ่งเป็นข้อมูลที่ catalog มีอยู่แล้วทุกแถว แล้วเอากฎทั่วไปของ SRD มาครอบ
/// ครั้งเดียวใช้ได้ทั้งอาวุธ/เวท/ท่าของมอนสเตอร์ — ไม่มีเงื่อนไขรายตัวใน catalog สักแถว
///
/// **สองกฎที่ใช้ ตรวจกับ SRD 5.2 ฉบับ CC แล้ว (SRD_CC_v5.2.pdf) ไม่ใช่เขียนจากความจำ:**
///   • *"Your attack roll has Disadvantage when your target is beyond normal range, and you can't
///     attack a target beyond long range."* → เกินระยะไกลสุด = ระดับ 1 (หายไปเลย) ·
///     ระหว่างระยะปกติกับระยะไกลสุด = ระดับ 2 (อยู่ในรายการ + ป้ายเหตุผล)
///   • *"When you make a ranged attack roll with a weapon, a spell, or some other means, you have
///     Disadvantage on the roll if you are within 5 feet of an enemy who can see you and doesn't
///     have the Incapacitated condition."* → ตัวอย่างที่ dispatch ยกไว้ ("ร่ายเวทระยะไกลตอนศัตรู
///     ประชิด → ติด disadvantage") เป็นกฎเดียวกันนี้ · สังเกตว่ากฎผูกกับ **การทอยโจมตีระยะไกล**
///     ไม่ใช่ "เวททุกอัน" — Magic Missile ที่ไม่มีทอยโจมตีจึงไม่ติด (ดูเทสต์ข้างล่าง)
///
/// **ความปลอดภัย (§1.2) เป็นเรื่องของ "รับอะไรเข้ามา" ไม่ใช่ "กรองอะไรออกไป"** — resolver รับเฉพาะ
/// เป้าที่ผู้ดูคนนั้นมองเห็นแล้ว (<c>visibleTargets</c>) จึง **ไม่มีทางพูดถึงสิ่งที่มองไม่เห็นได้เลย
/// เชิงโครงสร้าง** ไม่ใช่คำนวณจากความจริงทั้งหมดแล้วค่อยกรองทีหลัง (ซึ่งเป็นแพทเทิร์นที่ Sprint F
/// งานที่ 7 เคยพลาดมาแล้ว) · ชั้น API เป็นคนกรองหมอกก่อนเรียกที่นี่
/// </summary>
public class AffordanceResolverTests
{
    private static readonly Guid Longsword = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Shortbow = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid FireBolt = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid MagicMissile = Guid.Parse("00000000-0000-0000-0000-0000000000a4");

    private static readonly Guid Goblin = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Ogre = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Cleric = Guid.Parse("00000000-0000-0000-0000-0000000000b3");

    /// <summary>ดาบยาว: ประชิด 5 ฟุต ทอยโจมตี ใช้ action (ตรงกับแถว longsword ใน catalog seed)</summary>
    private static AffordanceOption MeleeWeapon() =>
        new(Longsword, "Longsword", ActionResourceCost.Action,
            RequiresAttackRoll: true, TargetsEnemy: true, NormalRangeFeet: 5, LongRangeFeet: null);

    /// <summary>ธนูสั้น 80/320 ทอยโจมตี (ตรงกับแถว shortbow ใน catalog seed)</summary>
    private static AffordanceOption RangedWeapon() =>
        new(Shortbow, "Shortbow", ActionResourceCost.Action,
            RequiresAttackRoll: true, TargetsEnemy: true, NormalRangeFeet: 80, LongRangeFeet: 320);

    /// <summary>Fire Bolt: เวทระยะ 120 ฟุต **มีทอยโจมตี** (spell attack roll)</summary>
    private static AffordanceOption RangedAttackSpell() =>
        new(FireBolt, "Fire Bolt", ActionResourceCost.Action,
            RequiresAttackRoll: true, TargetsEnemy: true, NormalRangeFeet: 120, LongRangeFeet: null);

    /// <summary>Magic Missile: เวทระยะ 120 ฟุต **ไม่มีทอยโจมตี** (auto-hit)</summary>
    private static AffordanceOption AutoHitSpell() =>
        new(MagicMissile, "Magic Missile", ActionResourceCost.Action,
            RequiresAttackRoll: false, TargetsEnemy: true, NormalRangeFeet: 120, LongRangeFeet: null);

    private static AffordanceTarget Enemy(Guid id, string name, int distanceFeet) =>
        new(id, name, IsEnemy: true, DistanceFeet: distanceFeet);

    private static AffordanceTarget Ally(Guid id, string name, int distanceFeet) =>
        new(id, name, IsEnemy: false, DistanceFeet: distanceFeet);

    private static ActionEconomyState FreshTurn() => ActionEconomyResolver.StartTurn(30);

    // ── ระดับ 3: ทำได้ปกติ ────────────────────────────────────────────────────

    [Fact]
    public void Melee_attack_on_adjacent_enemy_is_offered_with_no_warnings()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [MeleeWeapon()], [Enemy(Goblin, "ก็อบลิน", 5)]);

        var affordance = Assert.Single(result);
        Assert.Equal(Longsword, affordance.OptionId);
        Assert.Equal(Goblin, affordance.TargetId);
        Assert.Equal(AdvantageMode.None, affordance.AdvantageMode);
        Assert.Empty(affordance.Notes);
    }

    // ── ระดับ 1: ทำไม่ได้ = หายไปจากรายการเลย ────────────────────────────────

    /// <summary>ศัตรูอยู่ไกล 30 ฟุต แต่ถือดาบประชิด → ดาบต้องไม่อยู่ในรายการ ไม่ใช่ให้กดแล้วเด้ง error
    /// (§1.1: *"ศัตรูที่อยู่ไกลเกินไม่ปรากฏในลิสต์ตั้งแต่แรก"*)</summary>
    [Fact]
    public void Melee_attack_on_distant_enemy_is_not_offered_at_all()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [MeleeWeapon()], [Enemy(Goblin, "ก็อบลิน", 30)]);

        Assert.Empty(result);
    }

    /// <summary>SRD: *"you can't attack a target beyond long range"* → เกิน 320 ฟุตคือทำไม่ได้จริง
    /// ไม่ใช่ทำได้แบบมีราคา</summary>
    [Fact]
    public void Ranged_attack_beyond_long_range_is_not_offered_at_all()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [RangedWeapon()], [Enemy(Goblin, "ก็อบลิน", 321)]);

        Assert.Empty(result);
    }

    /// <summary>ท่าที่มีระยะเดียว (ไม่มีระยะไกลสุด) เกินระยะนั้นคือทำไม่ได้เลย — ไม่ใช่ไปเดาว่ามี
    /// ระยะไกลสุดซ่อนอยู่</summary>
    [Fact]
    public void Single_range_option_beyond_its_range_is_not_offered()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [RangedAttackSpell()], [Enemy(Goblin, "ก็อบลิน", 121)]);

        Assert.Empty(result);
    }

    /// <summary>ใช้ action ไปแล้วในเทิร์นนี้ → ท่าที่ต้องใช้ action หายไปทั้งหมด (§2.3 "ตัดตัวเลือกที่
    /// ทรัพยากรหมด") · ผู้เล่นไม่ต้องเดาว่าทำไมกดไม่ได้ เพราะมันไม่อยู่ให้กดตั้งแต่แรก</summary>
    [Fact]
    public void Options_costing_a_spent_resource_are_not_offered()
    {
        var spentAction = ActionEconomyResolver.SpendAction(FreshTurn());

        var result = AffordanceResolver.Resolve(
            spentAction, [MeleeWeapon(), RangedWeapon()], [Enemy(Goblin, "ก็อบลิน", 5)]);

        Assert.Empty(result);
    }

    /// <summary>
    /// **เทสต์ความปลอดภัยหลักของ Part นี้ (§1.2)** — ศัตรูที่ผู้ดูยังมองไม่เห็น (ยังไม่เปิดหมอกตรงนั้น)
    /// จะไม่ถูกส่งเข้ามาใน visibleTargets เลย ผลลัพธ์จึงต้องไม่พาดพิงถึงมันแม้แต่ทางอ้อม
    ///
    /// เทสต์นี้ยืนยันคุณสมบัติเชิงโครงสร้าง: resolver ไม่มี input ทางอื่นให้รู้จักศัตรูที่มองไม่เห็นเลย
    /// (ไม่รับ DbContext/สถานะโลกทั้งใบ) — ถ้ามีคนแก้ให้ resolver ไปดึงข้อมูลเองเมื่อไหร่ เทสต์นี้จะ
    /// ยังเขียวแต่ signature จะเปลี่ยน ซึ่งเป็นสัญญาณให้ reviewer จับได้ทันที
    /// </summary>
    [Fact]
    public void Enemies_not_passed_in_as_visible_produce_no_options_whatsoever()
    {
        // ก็อบลินยืนประชิดอยู่จริงในโลก แต่ผู้ดูยังไม่เห็น → ชั้น API ไม่ส่งเข้ามา
        var result = AffordanceResolver.Resolve(FreshTurn(), [MeleeWeapon(), RangedWeapon()], []);

        Assert.Empty(result);
    }

    // ── ระดับ 2: ทำได้ แต่มีราคา (หัวใจของ concept) ──────────────────────────

    /// <summary>
    /// SRD: *"Your attack roll has Disadvantage when your target is beyond normal range"* —
    /// ธนูสั้น 80/320 ยิงเป้าที่ 100 ฟุต = อยู่ในรายการ แต่ติด disadvantage พร้อมป้ายบอกเหตุผล
    /// </summary>
    [Fact]
    public void Ranged_attack_beyond_normal_range_is_offered_with_disadvantage_and_a_reason()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [RangedWeapon()], [Enemy(Goblin, "ก็อบลิน", 100)]);

        var affordance = Assert.Single(result);
        Assert.Equal(AdvantageMode.Disadvantage, affordance.AdvantageMode);
        var note = Assert.Single(affordance.Notes);
        Assert.Equal(AffordanceNoteCode.BeyondNormalRange, note.Code);
        // ป้ายต้องเป็นข้อความภาษาคนที่อ่านแล้วเข้าใจเลย ไม่ใช่รหัสให้ client ไปแปลเอง
        Assert.False(string.IsNullOrWhiteSpace(note.Message));
    }

    /// <summary>
    /// ตัวอย่างที่ dispatch ยกมาตรง ๆ: *"ร่ายเวทระยะไกลตอนศัตรูประชิด → ติด disadvantage"*
    ///
    /// ยิงเป้าที่อยู่ไกล 60 ฟุต (ในระยะปกติสบาย ๆ) แต่มีก็อบลินอีกตัวยืนประชิดผู้ร่ายอยู่ 5 ฟุต
    /// </summary>
    [Fact]
    public void Ranged_attack_while_an_enemy_is_adjacent_gets_disadvantage_and_a_reason()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(),
            [RangedAttackSpell()],
            [Enemy(Ogre, "ออร์ค", 60), Enemy(Goblin, "ก็อบลิน", 5)]);

        var atRange = result.Single(a => a.TargetId == Ogre);
        Assert.Equal(AdvantageMode.Disadvantage, atRange.AdvantageMode);
        Assert.Contains(atRange.Notes, n => n.Code == AffordanceNoteCode.RangedAttackWithEnemyAdjacent);
    }

    /// <summary>
    /// **กฎผูกกับ "การทอยโจมตีระยะไกล" ไม่ใช่ "เวท"** — Magic Missile ไม่มีทอยโจมตี (auto-hit)
    /// จึงไม่ติด disadvantage แม้ศัตรูประชิด · ถ้าเขียนกฎเป็น "เวทระยะไกล + ประชิด = disadvantage"
    /// ตามคำบรรยายลอย ๆ จะได้ผลผิดตรงนี้ทันที และผู้เล่นจะโดนโทษที่กฎไม่ได้กำหนด
    /// </summary>
    [Fact]
    public void Auto_hit_spell_while_enemy_adjacent_gets_no_disadvantage()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(),
            [AutoHitSpell()],
            [Enemy(Ogre, "ออร์ค", 60), Enemy(Goblin, "ก็อบลิน", 5)]);

        var atRange = result.Single(a => a.TargetId == Ogre);
        Assert.Equal(AdvantageMode.None, atRange.AdvantageMode);
        Assert.Empty(atRange.Notes);
    }

    /// <summary>โจมตีประชิดตอนศัตรูประชิดคือเรื่องปกติของการรบ ไม่ติดอะไรทั้งนั้น — กฎนี้เป็นของ
    /// **การโจมตีระยะไกล** เท่านั้น</summary>
    [Fact]
    public void Melee_attack_while_enemy_adjacent_gets_no_disadvantage()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [MeleeWeapon()], [Enemy(Goblin, "ก็อบลิน", 5)]);

        Assert.Equal(AdvantageMode.None, Assert.Single(result).AdvantageMode);
    }

    /// <summary>เข้าเงื่อนไขสองข้อพร้อมกัน (เกินระยะปกติ + มีศัตรูประชิด) → ยังเป็น disadvantage
    /// (ไม่ทบเป็น "disadvantage สองชั้น" ตามกฎ 2024) แต่ต้องได้ป้ายเหตุผล **ครบทั้งสองข้อ**
    /// เพราะผู้เล่นต้องรู้ราคาทั้งหมดของทางเลือกก่อนกด ไม่ใช่รู้แค่ข้อแรกที่เจอ</summary>
    [Fact]
    public void Two_disadvantage_sources_still_one_disadvantage_but_both_reasons_listed()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(),
            [RangedWeapon()],
            [Enemy(Ogre, "ออร์ค", 100), Enemy(Goblin, "ก็อบลิน", 5)]);

        var atRange = result.Single(a => a.TargetId == Ogre);
        Assert.Equal(AdvantageMode.Disadvantage, atRange.AdvantageMode);
        Assert.Equal(2, atRange.Notes.Count);
        Assert.Contains(atRange.Notes, n => n.Code == AffordanceNoteCode.BeyondNormalRange);
        Assert.Contains(atRange.Notes, n => n.Code == AffordanceNoteCode.RangedAttackWithEnemyAdjacent);
    }

    /// <summary>ก็อบลินที่ยืนประชิดเองก็ยังเป็นเป้าที่ยิงได้ (อยู่ในระยะ) — และการยิงมันก็ติด
    /// disadvantage เหมือนกัน เพราะกฎดูว่า "มีศัตรูประชิดตัวผู้ยิงไหม" ไม่ได้ดูว่ายิงใคร</summary>
    [Fact]
    public void Adjacent_enemy_is_still_a_valid_ranged_target_but_with_disadvantage()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [RangedWeapon()], [Enemy(Goblin, "ก็อบลิน", 5)]);

        var affordance = Assert.Single(result);
        Assert.Equal(Goblin, affordance.TargetId);
        Assert.Equal(AdvantageMode.Disadvantage, affordance.AdvantageMode);
    }

    // ── การจับคู่ท่ากับเป้า ──────────────────────────────────────────────────

    /// <summary>ท่าเดียวกับศัตรูที่มองเห็น 2 ตัว = 2 ตัวเลือกแยกกัน (พาเนลเป็นผลลัพธ์ที่คำนวณมาแล้ว
    /// ไม่ใช่ฟอร์มให้เลือกเป้าทีหลัง — §1.1)</summary>
    [Fact]
    public void One_option_against_two_visible_enemies_yields_two_entries()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [MeleeWeapon()], [Enemy(Goblin, "ก็อบลิน", 5), Enemy(Ogre, "ออร์ค", 5)]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, a => a.TargetId == Goblin);
        Assert.Contains(result, a => a.TargetId == Ogre);
    }

    /// <summary>ท่าที่เล็งศัตรูต้องไม่ไปจับคู่กับเพื่อนร่วมทีม — เป้าที่ผิดชนิดคือ "ทำไม่ได้"
    /// ระดับ 1 หายไปเลย</summary>
    [Fact]
    public void Enemy_targeting_option_is_not_paired_with_allies()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [MeleeWeapon()], [Ally(Cleric, "นักบวช", 5)]);

        Assert.Empty(result);
    }

    /// <summary>ชื่อเป้าต้องติดมากับผลลัพธ์ ไม่ใช่ให้ client ไปหาเอง — เพราะป้ายเหตุผลต้องอ่านรู้เรื่อง
    /// ในตัวมันเอง และ client ไม่ควรต้องมีตารางชื่อของสิ่งที่ตัวเองอาจไม่มีสิทธิ์เห็น</summary>
    [Fact]
    public void Target_name_travels_with_the_result()
    {
        var result = AffordanceResolver.Resolve(
            FreshTurn(), [MeleeWeapon()], [Enemy(Goblin, "ก็อบลิน", 5)]);

        Assert.Equal("ก็อบลิน", Assert.Single(result).TargetName);
        Assert.Equal("Longsword", Assert.Single(result).OptionName);
    }
}

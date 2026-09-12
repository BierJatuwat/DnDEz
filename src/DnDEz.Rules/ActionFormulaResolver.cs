namespace DnDEz.Rules;

/// <summary>ค่าความสามารถ 6 ค่าของ SRD 5.2 — Rules ประกาศเองไม่ผูกกับ Domain (เหตุผลเดียวกับ
/// <see cref="DamageType"/>: Rules เป็น zero-dependency)</summary>
public enum Ability
{
    Strength,
    Dexterity,
    Constitution,
    Intelligence,
    Wisdom,
    Charisma
}

/// <summary>
/// ability ตามที่แถวใน catalog ระบุ — ต่างจาก <see cref="Ability"/> ตรงที่มี
/// <see cref="Spellcasting"/> ซึ่ง **ยังไม่ใช่ ability จริง** จนกว่าจะรู้คลาสของผู้ร่าย
/// (เวทเดียวกันใช้ INT เมื่อ Wizard ร่าย แต่ใช้ WIS เมื่อ Cleric ร่าย — catalog เก็บเป็นค่าเดียวไม่ได้)
/// </summary>
public enum ActionAbilitySource
{
    Strength,
    Dexterity,
    Spellcasting
}

/// <summary>รูปทอยของท่า — ชุดเดียวกับ <c>ActionRollKind</c> ใน Domain แต่ประกาศซ้ำที่นี่ด้วยเหตุผลเดียวกัน</summary>
public enum FormulaRollKind
{
    AttackRoll,
    SavingThrow,
    None
}

/// <summary>ค่าความสามารถ 6 ค่าของผู้กระทำ (score ดิบ ไม่ใช่ modifier)</summary>
public sealed record AbilityScores(
    int Strength,
    int Dexterity,
    int Constitution,
    int Intelligence,
    int Wisdom,
    int Charisma);

/// <summary>
/// ข้อมูลของท่าหนึ่งท่าที่ย่อยมาจากแถว catalog เหลือเฉพาะที่ใช้ประกอบสูตร
/// </summary>
/// <param name="RollKind">ทอยโจมตี / ให้เป้า save / ไม่ทอย</param>
/// <param name="Ability">ability ที่ท่านี้ใช้ — null = ท่าที่ไม่พึ่ง ability เลย (เช่น Magic Missile)</param>
/// <param name="DamageDice">ลูกเต๋าดาเมจของท่าเอง **ยังไม่รวม modifier ของผู้กระทำ** เช่น "1d8"</param>
/// <param name="DamageType">ชนิดดาเมจ — มีค่าก็ต่อเมื่อ <paramref name="DamageDice"/> มีค่า</param>
/// <param name="HealingDice">ลูกเต๋าการรักษา เช่น "2d8" (Cure Wounds)</param>
/// <param name="AddsAbilityToEffect">บวก ability modifier เข้าดาเมจ/การรักษาไหม</param>
/// <param name="IsCantrip">เป็น cantrip ไหม (spell_slot_level = 0)</param>
public sealed record ActionFormulaSource(
    FormulaRollKind RollKind,
    ActionAbilitySource? Ability,
    string? DamageDice,
    DamageType? DamageType,
    string? HealingDice,
    bool AddsAbilityToEffect,
    bool IsCantrip);

/// <summary>
/// สูตรที่ประกอบเสร็จแล้ว พร้อมส่งเข้า <see cref="DiceExpressionParser"/>/ endpoint โจมตีเดิม
/// ผู้เล่นจึงไม่ต้องกรอก "1d20+5" เองอีก
/// </summary>
/// <param name="Ability">ability จริงหลัง resolve แล้ว (Spellcasting → INT/WIS ตามคลาส)</param>
/// <param name="AttackExpression">สูตรทอยโจมตี เช่น "1d20+4" — มีเฉพาะท่าที่ทอยโจมตี</param>
/// <param name="SaveDc">DC ที่เป้าต้อง save ให้ผ่าน — มีเฉพาะท่าแบบ saving throw</param>
/// <param name="DamageExpression">สูตรดาเมจรวม modifier แล้ว เช่น "1d8+2"</param>
/// <param name="DamageType">ชนิดดาเมจ</param>
/// <param name="HealingExpression">สูตรการรักษารวม modifier แล้ว เช่น "2d8+2"</param>
public sealed record ActionFormula(
    Ability? Ability,
    string? AttackExpression,
    int? SaveDc,
    string? DamageExpression,
    DamageType? DamageType,
    string? HealingExpression);

/// <summary>
/// Sprint H Part 4 — ประกอบสูตรทอยโจมตี/DC/ดาเมจ/การรักษา จาก catalog + ค่าของผู้กระทำ แบบ
/// deterministic ล้วน (ไม่มี I/O ไม่มี RNG) · ผู้เล่นเลิกกรอก "1d20+5"/"1d8+3" เองได้เพราะไฟล์นี้
///
/// **ทุกกฎด้านล่างอ้าง SRD 5.2 ฉบับ CC (SRD_CC_v5.2.pdf, `pdftotext -raw`) ไม่ใช่ความจำ:**
/// <list type="bullet">
///   <item>ตาราง Ability Modifiers: 1 → −5, 2–3 → −4, … 10–11 → +0, … 30 → +10</item>
///   <item>*"Melee attack bonus = Strength modifier + Proficiency Bonus"* ·
///     *"Ranged attack bonus = Dexterity modifier + Proficiency Bonus"*</item>
///   <item>*"You add the same ability modifier you use for attacks with a weapon to your damage
///     rolls with that weapon."*</item>
///   <item>*"A spell tells you which dice to roll for damage and whether to add any modifiers."*
///     → เวทบวก modifier เฉพาะเมื่อตัวบทของเวทนั้นบอก (catalog เก็บเป็น
///     <see cref="ActionFormulaSource.AddsAbilityToEffect"/>)</item>
///   <item>*"Spell attack modifier = your spellcasting ability modifier + your Proficiency Bonus"*</item>
///   <item>*"Spell save DC = 8 + your spellcasting ability modifier + your Proficiency Bonus"*</item>
///   <item>ตาราง Character Advancement: Proficiency Bonus +2 (เลเวล 1–4), +3 (5–8), +4 (9–12),
///     +5 (13–16), +6 (17–20)</item>
///   <item>Cleric: *"Wisdom is your spellcasting ability for your Cleric spells."* ·
///     Wizard: *"Intelligence is your spellcasting ability for your Wizard spells."*</item>
/// </list>
///
/// ## คืน null = "ประกอบสูตรให้อย่างปลอดภัยไม่ได้" → frontend กลับไปให้กรอกเอง
/// ดีกว่าเดาตัวเลขแล้วหลอกผู้เล่นเงียบ ๆ — ตัวเลขผิดบนพาเนลไม่มีอะไรจับได้เลย
/// เงื่อนไขที่คืน null ครบทุกข้อ (ห้ามเพิ่มกรณีเดาเอง):
/// <list type="number">
///   <item>เลเวลนอกช่วง <see cref="MinLevel"/>–<see cref="MaxLevel"/> (ตาราง proficiency มีแค่ 1–20)</item>
///   <item>เป็น cantrip และเลเวล ≥ <see cref="FirstCantripUpgradeLevel"/> — ตัวบท Fire Bolt/Sacred
///     Flame: *"The damage increases by 1d10 when you reach levels 5 (2d10), 11 (3d10), and 17
///     (4d10)."* การขยายลูกเต๋าตามเลเวล **ยังไม่ได้ทำโมเดล** ถ้าคืนสูตรเลเวล 1 ไปให้ตัวเลเวล 5
///     ผู้เล่นจะได้ดาเมจครึ่งเดียวของที่กฎให้โดยไม่รู้ตัว</item>
///   <item>ท่าใช้ Spellcasting แต่คลาสไม่อยู่ใน <see cref="SpellcastingAbilityForClass"/></item>
///   <item>ท่าต้องใช้ ability (ทอยโจมตี / save / บวกเข้าผล) แต่ไม่มี ability ให้ใช้</item>
///   <item>score ของ ability ที่ **ถูกใช้จริง** อยู่นอก <see cref="MinAbilityScore"/>–
///     <see cref="MaxAbilityScore"/> (score ของ ability อื่นที่ท่านี้ไม่ใช้ ไม่ต้องเช็ค)</item>
///   <item>ลูกเต๋าดาเมจกับชนิดดาเมจมาไม่ครบคู่ — สูตรดาเมจที่ไม่มีชนิดหัก resistance ไม่ได้ · DB มี
///     CHECK <c>ck_damage_pair_actioncatalogentry</c> กันอยู่แล้ว แต่ resolver ไม่พึ่งคนเรียกในการ
///     รักษาสัญญา "ไม่เดา" ของตัวเอง</item>
/// </list>
///
/// ⚠️ **สมมติว่าผู้กระทำ proficient กับทุกท่าที่ถืออยู่เสมอ** — ระบบยังไม่มีข้อมูล proficiency
/// รายตัวละคร · ทุกวันนี้ท่ามาจาก preset ซึ่งเลือกอาวุธที่คลาสนั้นฝึกใช้ได้อยู่แล้ว ข้อสมมตินี้จึงตรง
/// กับข้อมูลที่มี แต่ต้องกลับมาแก้เมื่อผู้เล่นถืออาวุธนอก preset ได้
///
/// รูปแบบสูตร: modifier รวมเป็น 0 ไม่ต่อท้าย ("1d8" ไม่ใช่ "1d8+0") · บวกเป็น "+N" · ลบเป็น "-N"
/// </summary>
public static class ActionFormulaResolver
{
    public const int MinLevel = 1;
    public const int MaxLevel = 20;
    public const int MinAbilityScore = 1;
    public const int MaxAbilityScore = 30;

    /// <summary>เลเวลแรกที่ cantrip ขยายลูกเต๋า (ตัวบทเวท: "levels 5 (2d10), 11 (3d10), and 17")</summary>
    public const int FirstCantripUpgradeLevel = 5;

    /// <summary>ตาราง Ability Modifiers ของ SRD 5.2 · score นอก 1–30 โยน ArgumentOutOfRangeException</summary>
    public static int AbilityModifier(int score)
    {
        if (score < MinAbilityScore || score > MaxAbilityScore)
            throw new ArgumentOutOfRangeException(nameof(score));

        return (int)Math.Floor((score - 10) / 2.0);
    }

    /// <summary>ตาราง Character Advancement ของ SRD 5.2 · เลเวลนอก 1–20 โยน ArgumentOutOfRangeException</summary>
    public static int ProficiencyBonus(int level)
    {
        if (level < MinLevel || level > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(level));

        return (level - 1) / 4 + 2;
    }

    /// <summary>
    /// ability ที่ใช้ร่ายเวทของคลาสนั้น (ไม่สนตัวพิมพ์เล็กใหญ่) — **มีเฉพาะคลาสที่ตรวจตัวบท SRD แล้ว**:
    /// Cleric → Wisdom, Wizard → Intelligence · คลาสอื่น/null/สตริงว่าง → null
    /// (ห้ามเติมคลาสอื่นจากความจำ ต้องเปิดตัวบทก่อน)
    /// </summary>
    public static Ability? SpellcastingAbilityForClass(string? className)
    {
        if (string.Equals(className, "Wizard", StringComparison.OrdinalIgnoreCase))
            return Ability.Intelligence;

        if (string.Equals(className, "Cleric", StringComparison.OrdinalIgnoreCase))
            return Ability.Wisdom;

        return null;
    }

    /// <summary>ประกอบสูตรทั้งหมดของท่าหนึ่งท่า — ดูเงื่อนไขคืน null ที่ doc comment ของคลาส</summary>
    public static ActionFormula? Build(
        ActionFormulaSource source, AbilityScores scores, int level, string? className)
    {
        if (level < MinLevel || level > MaxLevel)
            return null;

        if (source.IsCantrip && level >= FirstCantripUpgradeLevel)
            return null;

        if ((source.DamageDice is null) != (source.DamageType is null))
            return null;

        Ability? resolvedAbility = null;
        if (source.Ability is not null)
        {
            resolvedAbility = source.Ability.Value switch
            {
                ActionAbilitySource.Strength => Ability.Strength,
                ActionAbilitySource.Dexterity => Ability.Dexterity,
                ActionAbilitySource.Spellcasting => SpellcastingAbilityForClass(className),
                _ => null,
            };

            if (source.Ability.Value == ActionAbilitySource.Spellcasting && resolvedAbility is null)
                return null;
        }

        var needsAbility = source.RollKind is FormulaRollKind.AttackRoll or FormulaRollKind.SavingThrow
            || source.AddsAbilityToEffect;
        if (needsAbility && resolvedAbility is null)
            return null;

        var mod = 0;
        if (resolvedAbility is not null)
        {
            var score = resolvedAbility.Value switch
            {
                Ability.Strength => scores.Strength,
                Ability.Dexterity => scores.Dexterity,
                Ability.Intelligence => scores.Intelligence,
                Ability.Wisdom => scores.Wisdom,
                _ => throw new InvalidOperationException($"ability {resolvedAbility} ไม่ควรถูก resolve ที่นี่"),
            };

            if (score < MinAbilityScore || score > MaxAbilityScore)
                return null;

            mod = AbilityModifier(score);
        }

        var proficiencyBonus = ProficiencyBonus(level);

        var attackExpression = source.RollKind == FormulaRollKind.AttackRoll
            ? "1d20" + Signed(mod + proficiencyBonus)
            : null;
        var saveDc = source.RollKind == FormulaRollKind.SavingThrow
            ? 8 + mod + proficiencyBonus
            : (int?)null;
        var damageExpression = source.DamageDice is null
            ? null
            : source.DamageDice + (source.AddsAbilityToEffect ? Signed(mod) : "");
        var healingExpression = source.HealingDice is null
            ? null
            : source.HealingDice + (source.AddsAbilityToEffect ? Signed(mod) : "");

        return new ActionFormula(
            resolvedAbility, attackExpression, saveDc, damageExpression, source.DamageType, healingExpression);
    }

    /// <summary>+N / -N ตามเครื่องหมาย · 0 คืนสตริงว่างเพื่อไม่ให้ต่อท้าย "+0"</summary>
    private static string Signed(int n) => n switch
    {
        > 0 => "+" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        < 0 => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => string.Empty,
    };
}

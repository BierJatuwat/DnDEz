namespace DnDEz.Rules.Tests;

/// <summary>
/// Sprint H Part 4 — ประกอบสูตรทอยโจมตี/DC/ดาเมจ/การรักษาจาก catalog + ค่าของผู้กระทำ
///
/// ตัวเลขที่คาดหวังทุกตัวคำนวณจากตัวบท SRD 5.2 ฉบับ CC (ดู doc comment ของ ActionFormulaResolver)
/// และ stat line ของ preset ใน migration `SeedActionCatalogAdditionsAndCharacterPresets`
/// (standard array 15/14/13/12/10/8) — เลือกใช้ stat line จริงของ preset เพราะนี่คือตัวเลขที่ผู้เล่น
/// จะเห็นบนพาเนลจริงตั้งแต่วันแรก
/// </summary>
public class ActionFormulaResolverTests
{
    // ── stat line ของ preset (ตรงกับ migration ทุกตัว) ──
    private static readonly AbilityScores Fighter = new(15, 13, 14, 12, 10, 8);
    private static readonly AbilityScores Rogue = new(8, 15, 14, 13, 12, 10);
    private static readonly AbilityScores Barbarian = new(15, 13, 14, 8, 12, 10);
    private static readonly AbilityScores Wizard = new(8, 14, 13, 15, 12, 10);
    private static readonly AbilityScores Cleric = new(12, 13, 14, 8, 15, 10);

    // ── แถว catalog (ค่าเดียวกับที่ seed — ดู ActionCatalogDamageSeedTests) ──
    private static readonly ActionFormulaSource Longsword =
        new(FormulaRollKind.AttackRoll, ActionAbilitySource.Strength, "1d8", DamageType.Slashing, null, true, false);
    private static readonly ActionFormulaSource Shortbow =
        new(FormulaRollKind.AttackRoll, ActionAbilitySource.Dexterity, "1d6", DamageType.Piercing, null, true, false);
    private static readonly ActionFormulaSource Greataxe =
        new(FormulaRollKind.AttackRoll, ActionAbilitySource.Strength, "1d12", DamageType.Slashing, null, true, false);
    private static readonly ActionFormulaSource FireBolt =
        new(FormulaRollKind.AttackRoll, ActionAbilitySource.Spellcasting, "1d10", DamageType.Fire, null, false, true);
    private static readonly ActionFormulaSource MagicMissile =
        new(FormulaRollKind.None, null, "3d4+3", DamageType.Force, null, false, false);
    private static readonly ActionFormulaSource SacredFlame =
        new(FormulaRollKind.SavingThrow, ActionAbilitySource.Spellcasting, "1d8", DamageType.Radiant, null, false, true);
    private static readonly ActionFormulaSource CureWounds =
        new(FormulaRollKind.None, ActionAbilitySource.Spellcasting, null, null, "2d8", true, false);

    // ── ตาราง Ability Modifiers (SRD 5.2) — ไล่ทุกขอบของทุกช่วง ไม่ใช่แค่ค่ากลาง ──

    [Theory]
    [InlineData(1, -5)]
    [InlineData(2, -4)]
    [InlineData(3, -4)]
    [InlineData(4, -3)]
    [InlineData(5, -3)]
    [InlineData(8, -1)]
    [InlineData(9, -1)]
    [InlineData(10, 0)]
    [InlineData(11, 0)]
    [InlineData(12, 1)]
    [InlineData(15, 2)]
    [InlineData(16, 3)]
    [InlineData(17, 3)]
    [InlineData(20, 5)]
    [InlineData(29, 9)]
    [InlineData(30, 10)]
    public void Ability_modifier_matches_the_srd_table(int score, int expected)
    {
        Assert.Equal(expected, ActionFormulaResolver.AbilityModifier(score));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Ability_score_outside_the_srd_table_throws(int score)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionFormulaResolver.AbilityModifier(score));
    }

    // ── ตาราง Proficiency Bonus (SRD 5.2) — ทุกขอบของทุกช่วง ──

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 4)]
    [InlineData(12, 4)]
    [InlineData(13, 5)]
    [InlineData(16, 5)]
    [InlineData(17, 6)]
    [InlineData(20, 6)]
    public void Proficiency_bonus_matches_the_srd_table(int level, int expected)
    {
        Assert.Equal(expected, ActionFormulaResolver.ProficiencyBonus(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Level_outside_the_srd_table_throws(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionFormulaResolver.ProficiencyBonus(level));
    }

    // ── ability ที่ใช้ร่ายเวท — เฉพาะคลาสที่ตรวจตัวบทแล้ว ──

    [Theory]
    [InlineData("Wizard", Ability.Intelligence)]
    [InlineData("Cleric", Ability.Wisdom)]
    [InlineData("wizard", Ability.Intelligence)]
    [InlineData("CLERIC", Ability.Wisdom)]
    public void Spellcasting_ability_comes_from_the_class(string className, Ability expected)
    {
        Assert.Equal(expected, ActionFormulaResolver.SpellcastingAbilityForClass(className));
    }

    [Theory]
    [InlineData("Fighter")]
    [InlineData("Barbarian")]
    [InlineData("")]
    [InlineData(null)]
    public void Class_without_a_verified_spellcasting_ability_gives_null(string? className)
    {
        Assert.Null(ActionFormulaResolver.SpellcastingAbilityForClass(className));
    }

    // ── preset จริง × ท่าที่ถือ — ตัวเลขที่ผู้เล่นจะเห็นบนพาเนล ──

    /// <summary>Fighter STR 15 (+2) + PB 2 → โจมตี +4 · ดาบบวก STR เข้าดาเมจ (+2)</summary>
    [Fact]
    public void Fighter_longsword_uses_strength_for_attack_and_damage()
    {
        var f = ActionFormulaResolver.Build(Longsword, Fighter, 1, "Fighter");

        Assert.NotNull(f);
        Assert.Equal(Ability.Strength, f.Ability);
        Assert.Equal("1d20+4", f.AttackExpression);
        Assert.Equal("1d8+2", f.DamageExpression);
        Assert.Equal(DamageType.Slashing, f.DamageType);
        Assert.Null(f.SaveDc);
        Assert.Null(f.HealingExpression);
    }

    /// <summary>ธนูเป็นอาวุธระยะไกล → DEX (Rogue DEX 15 = +2) ไม่ใช่ STR 8</summary>
    [Fact]
    public void Rogue_shortbow_uses_dexterity_not_strength()
    {
        var f = ActionFormulaResolver.Build(Shortbow, Rogue, 1, "Rogue");

        Assert.NotNull(f);
        Assert.Equal(Ability.Dexterity, f.Ability);
        Assert.Equal("1d20+4", f.AttackExpression);
        Assert.Equal("1d6+2", f.DamageExpression);
        Assert.Equal(DamageType.Piercing, f.DamageType);
    }

    [Fact]
    public void Barbarian_greataxe_rolls_a_d12_plus_strength()
    {
        var f = ActionFormulaResolver.Build(Greataxe, Barbarian, 1, "Barbarian");

        Assert.NotNull(f);
        Assert.Equal("1d20+4", f.AttackExpression);
        Assert.Equal("1d12+2", f.DamageExpression);
        Assert.Equal(DamageType.Slashing, f.DamageType);
    }

    /// <summary>
    /// Fire Bolt: spell attack = INT (+2) + PB (+2) · **ดาเมจไม่บวก INT** — ตัวบทเขียนแค่
    /// *"the target takes 1d10 Fire damage"* ไม่ได้บอกให้บวก modifier (ต่างจากอาวุธ)
    /// </summary>
    [Fact]
    public void Wizard_fire_bolt_adds_intelligence_to_the_attack_but_not_to_damage()
    {
        var f = ActionFormulaResolver.Build(FireBolt, Wizard, 1, "Wizard");

        Assert.NotNull(f);
        Assert.Equal(Ability.Intelligence, f.Ability);
        Assert.Equal("1d20+4", f.AttackExpression);
        Assert.Equal("1d10", f.DamageExpression);
        Assert.Equal(DamageType.Fire, f.DamageType);
    }

    /// <summary>Magic Missile ไม่ทอยโจมตี ไม่มี save ไม่พึ่ง ability — สูตรเป็นของเวทล้วน</summary>
    [Fact]
    public void Magic_missile_has_no_attack_no_save_and_no_ability()
    {
        var f = ActionFormulaResolver.Build(MagicMissile, Wizard, 1, "Wizard");

        Assert.NotNull(f);
        Assert.Null(f.Ability);
        Assert.Null(f.AttackExpression);
        Assert.Null(f.SaveDc);
        Assert.Equal("3d4+3", f.DamageExpression);
        Assert.Equal(DamageType.Force, f.DamageType);
    }

    /// <summary>Sacred Flame: DC = 8 + WIS (+2) + PB (+2) = 12 · ไม่มีทอยโจมตี · ดาเมจไม่บวก WIS</summary>
    [Fact]
    public void Cleric_sacred_flame_gives_a_save_dc_instead_of_an_attack_roll()
    {
        var f = ActionFormulaResolver.Build(SacredFlame, Cleric, 1, "Cleric");

        Assert.NotNull(f);
        Assert.Equal(Ability.Wisdom, f.Ability);
        Assert.Null(f.AttackExpression);
        Assert.Equal(12, f.SaveDc);
        Assert.Equal("1d8", f.DamageExpression);
        Assert.Equal(DamageType.Radiant, f.DamageType);
    }

    /// <summary>Cure Wounds: *"2d8 plus your spellcasting ability modifier"* → WIS +2 · ไม่มีดาเมจเลย</summary>
    [Fact]
    public void Cleric_cure_wounds_heals_2d8_plus_wisdom_and_deals_no_damage()
    {
        var f = ActionFormulaResolver.Build(CureWounds, Cleric, 1, "Cleric");

        Assert.NotNull(f);
        Assert.Equal(Ability.Wisdom, f.Ability);
        Assert.Equal("2d8+2", f.HealingExpression);
        Assert.Null(f.DamageExpression);
        Assert.Null(f.DamageType);
        Assert.Null(f.AttackExpression);
        Assert.Null(f.SaveDc);
    }

    // ── รูปแบบสูตร ──

    /// <summary>modifier 0 ต้องไม่ต่อท้าย "+0" — STR 10 → โจมตีมีแค่ PB</summary>
    [Fact]
    public void Zero_modifier_is_omitted_from_the_expression()
    {
        var f = ActionFormulaResolver.Build(Longsword, new AbilityScores(10, 10, 10, 10, 10, 10), 1, null);

        Assert.NotNull(f);
        Assert.Equal("1d20+2", f.AttackExpression);
        Assert.Equal("1d8", f.DamageExpression);
    }

    /// <summary>STR 8 (−1): โจมตี −1+2 = +1 · ดาเมจติดลบได้ในสูตร (DamageResolver กันไม่ให้ต่ำกว่า 0 เอง
    /// ตามตัวบท *"it's possible to deal 0 damage but not negative damage"*)</summary>
    [Fact]
    public void Negative_modifier_is_written_with_a_minus_sign()
    {
        var f = ActionFormulaResolver.Build(Longsword, Wizard, 1, "Wizard");

        Assert.NotNull(f);
        Assert.Equal("1d20+1", f.AttackExpression);
        Assert.Equal("1d8-1", f.DamageExpression);
    }

    /// <summary>STR 1 (−5) + PB 2 = −3 → โจมตีรวมติดลบต้องเป็น "1d20-3"</summary>
    [Fact]
    public void Negative_attack_total_is_written_with_a_minus_sign()
    {
        var f = ActionFormulaResolver.Build(Longsword, new AbilityScores(1, 10, 10, 10, 10, 10), 1, null);

        Assert.NotNull(f);
        Assert.Equal("1d20-3", f.AttackExpression);
        Assert.Equal("1d8-5", f.DamageExpression);
    }

    /// <summary>
    /// เครื่องหมายลบต้องเป็น ASCII '-' เสมอไม่ว่าเครื่องตั้ง locale อะไร — <c>int.ToString()</c> ใช้
    /// <c>NumberFormatInfo.NegativeSign</c> ของ culture ปัจจุบัน และบาง locale (เช่น sv-SE บน ICU) ใช้
    /// U+2212 ซึ่ง <see cref="DiceExpressionParser"/> ไม่รับ → สูตรที่ server ส่งให้จะกดโจมตีไม่ได้เลย
    /// บนเครื่องที่ตั้งภาษาแบบนั้น
    /// </summary>
    [Fact]
    public void Negative_modifier_uses_an_ascii_minus_regardless_of_culture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");

            var f = ActionFormulaResolver.Build(Longsword, new AbilityScores(1, 10, 10, 10, 10, 10), 1, null);

            Assert.NotNull(f);
            Assert.Equal("1d20-3", f.AttackExpression);
            Assert.Equal("1d8-5", f.DamageExpression);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>เลเวล 5 → PB 3 เข้าโจมตี แต่ **ไม่เข้าดาเมจ** (ดาเมจบวกแค่ ability modifier)</summary>
    [Fact]
    public void Proficiency_bonus_goes_into_the_attack_but_never_into_damage()
    {
        var f = ActionFormulaResolver.Build(Longsword, Fighter, 5, "Fighter");

        Assert.NotNull(f);
        Assert.Equal("1d20+5", f.AttackExpression);
        Assert.Equal("1d8+2", f.DamageExpression);
    }

    [Fact]
    public void Every_expression_for_every_preset_action_parses()
    {
        var cases = new (ActionFormulaSource Source, AbilityScores Scores, string Class)[]
        {
            (Longsword, Fighter, "Fighter"), (Shortbow, Rogue, "Rogue"), (Greataxe, Barbarian, "Barbarian"),
            (FireBolt, Wizard, "Wizard"), (MagicMissile, Wizard, "Wizard"),
            (SacredFlame, Cleric, "Cleric"), (CureWounds, Cleric, "Cleric"),
        };

        foreach (var (source, scores, className) in cases)
        {
            var f = ActionFormulaResolver.Build(source, scores, 1, className);
            Assert.NotNull(f);
            foreach (var expression in new[] { f.AttackExpression, f.DamageExpression, f.HealingExpression })
            {
                if (expression is not null)
                {
                    DiceExpressionParser.Parse(expression);
                }
            }
        }
    }

    // ── กรณีที่ต้องคืน null (fail-safe: ไม่เดาตัวเลข) ──

    /// <summary>cantrip เลเวล 5 ขึ้นไปขยายลูกเต๋า (ยังไม่ทำโมเดล) → ห้ามคืนสูตรเลเวล 1</summary>
    [Fact]
    public void Cantrip_at_the_first_upgrade_level_is_not_computed()
    {
        Assert.Null(ActionFormulaResolver.Build(FireBolt, Wizard, 5, "Wizard"));
        Assert.Null(ActionFormulaResolver.Build(SacredFlame, Cleric, 5, "Cleric"));
    }

    [Fact]
    public void Cantrip_just_below_the_upgrade_level_is_still_computed()
    {
        var f = ActionFormulaResolver.Build(FireBolt, Wizard, 4, "Wizard");

        Assert.NotNull(f);
        Assert.Equal("1d10", f.DamageExpression);
    }

    /// <summary>Magic Missile เป็นเวทระดับ 1 ไม่ใช่ cantrip — เลเวลสูงขึ้นไม่เปลี่ยนสูตร และไม่ใช่
    /// upcast (Part นี้ห้ามทำ upcast: สูตรคือช่องเวทระดับ 1 เสมอ)</summary>
    [Fact]
    public void Leveled_spell_is_not_affected_by_the_cantrip_rule()
    {
        var f = ActionFormulaResolver.Build(MagicMissile, Wizard, 5, "Wizard");

        Assert.NotNull(f);
        Assert.Equal("3d4+3", f.DamageExpression);
    }

    [Theory]
    [InlineData("Fighter")]
    [InlineData(null)]
    public void Spellcasting_action_without_a_known_casting_class_is_not_computed(string? className)
    {
        Assert.Null(ActionFormulaResolver.Build(FireBolt, Wizard, 1, className));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Level_outside_the_table_is_not_computed(int level)
    {
        Assert.Null(ActionFormulaResolver.Build(Longsword, Fighter, level, "Fighter"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Used_ability_score_outside_the_table_is_not_computed(int strength)
    {
        Assert.Null(ActionFormulaResolver.Build(Longsword, Fighter with { Strength = strength }, 1, "Fighter"));
    }

    /// <summary>
    /// ลูกเต๋าดาเมจกับชนิดดาเมจต้องมาคู่กันเสมอ — ได้มาไม่ครบคู่ต้องคืน null ไม่ใช่ส่งสูตรดาเมจที่ไม่มีชนิด
    /// ออกไป (หัก resistance ไม่ได้ และ endpoint โจมตีจะบันทึกผิด) · DB มี CHECK
    /// <c>ck_damage_pair_actioncatalogentry</c> กันอยู่แล้ว แต่ resolver ต้องไม่พึ่งคนเรียกในการรักษาสัญญา
    /// "ไม่เดา" ของตัวเอง (พบในรอบ code review ของ Part นี้)
    /// </summary>
    [Fact]
    public void Damage_dice_without_a_damage_type_is_not_computed()
    {
        Assert.Null(ActionFormulaResolver.Build(Longsword with { DamageType = null }, Fighter, 1, "Fighter"));
    }

    [Fact]
    public void Damage_type_without_damage_dice_is_not_computed()
    {
        Assert.Null(ActionFormulaResolver.Build(Longsword with { DamageDice = null }, Fighter, 1, "Fighter"));
    }

    /// <summary>score ของ ability ที่ท่านี้ **ไม่ได้ใช้** ต้องไม่ทำให้ท่าพัง — ดาบใช้แค่ STR</summary>
    [Fact]
    public void Unused_ability_score_outside_the_table_is_ignored()
    {
        var f = ActionFormulaResolver.Build(Longsword, Fighter with { Intelligence = 0 }, 1, "Fighter");

        Assert.NotNull(f);
        Assert.Equal("1d20+4", f.AttackExpression);
    }

    /// <summary>ท่าที่ไม่ทอย ไม่มีดาเมจ ไม่มีรักษา (เช่นท่าเคลื่อนที่) → ได้สูตรที่ว่างทุกช่อง ไม่ใช่ null
    /// (null สงวนไว้สำหรับ "คำนวณให้ไม่ได้" เท่านั้น)</summary>
    [Fact]
    public void Action_with_nothing_to_roll_gives_an_empty_formula_not_null()
    {
        var walk = new ActionFormulaSource(FormulaRollKind.None, null, null, null, null, false, false);

        var f = ActionFormulaResolver.Build(walk, Fighter, 1, "Fighter");

        Assert.NotNull(f);
        Assert.Equal(new ActionFormula(null, null, null, null, null, null), f);
    }
}

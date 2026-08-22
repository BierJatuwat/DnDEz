using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// ปิดช่องว่างที่ code review ของ Phase 2 ชี้ไว้หลัง green
/// (แยกไฟล์ต่างหากเพื่อไม่แตะเทสต์ชุดเดิมที่ owner เขียน — แนวทางเดียวกับ ReviewFollowUpTests.cs ของ Phase 1)
/// </summary>
public class Phase2ReviewFollowUpTests
{
    private static readonly IReadOnlySet<DamageType> Empty = new HashSet<DamageType>();

    // --- code review ข้อ 1: negative rawAmount (เช่น "1d4-6") ไหลเข้า resistance math ตรง ๆ
    // ทำให้ damage ติดลบหรือใช้ C# truncation-toward-zero แทน floor ---

    [Fact]
    public void RollAndResolve_NegativeRawTotal_NoResistance_ClampsAdjustedAmountToZero()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(1); // "1d4-6" -> 1 + (-6) = -5
        var components = new[] { new DamageComponent("1d4-6", DamageType.Slashing) };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, Empty, Empty, Empty, seed: 1);

        var component = Assert.Single(result.Components);
        // RawAmount เก็บค่าดิบจริงไว้เพื่อ audit แม้จะติดลบ (สอดคล้องกับปรัชญาความโปร่งใสของ Phase 1
        // ที่ลูกเต๋าที่ถูก drop ยังถูกเก็บไว้ใน RollResult.Rolls) — ส่วนที่ต้อง clamp คือค่าที่เอาไปหัก HP จริง
        Assert.Equal(-5, component.RawAmount);
        Assert.Equal(0, component.AdjustedAmount);
        Assert.Equal(0, result.TotalAdjustedAmount);
    }

    [Fact]
    public void RollAndResolve_NegativeRawTotal_WithResistance_StillClampsToZero_NeverNegative()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(1); // "1d4-6" -> -5
        var components = new[] { new DamageComponent("1d4-6", DamageType.Fire) };
        var resistances = new HashSet<DamageType> { DamageType.Fire };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, resistances, Empty, Empty, seed: 1);

        var component = Assert.Single(result.Components);
        Assert.Equal(0, component.AdjustedAmount);
        Assert.Equal(0, result.TotalAdjustedAmount);
    }

    [Fact]
    public void RollAndResolve_NegativeRawTotal_MixedWithPositiveComponent_TotalNeverGoesBelowPositiveComponent()
    {
        var engine = new DiceEngine();
        // component 1: "1d4-6" slashing -> 1-6 = -5 -> clamp to 0
        // component 2: "1d6" fire -> 4 -> no resistance -> 4
        var roller = new SequenceDiceRoller(1, 4);
        var components = new[]
        {
            new DamageComponent("1d4-6", DamageType.Slashing),
            new DamageComponent("1d6", DamageType.Fire)
        };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, Empty, Empty, Empty, seed: 1);

        Assert.Equal(4, result.TotalAdjustedAmount); // 0 + 4 ไม่ใช่ -5 + 4 = -1
    }

    // --- code review ข้อ 2: AttackResolver หา d20 ที่ไม่ถูก drop "ตัวแรกที่เจอ" โดยไม่ตรวจว่ามีเกิน 1 ตัว
    // เช่น "2d20+5" ที่ไม่ได้ผ่าน advantage/disadvantage (ไม่โดน ValidateAdvantageShape กัน) ---

    [Fact]
    public void Resolve_MultipleUndroppedD20s_ThrowsInvalidOperationException()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10, 15);
        var roll = engine.RollSystem("2d20+5", roller, seed: 1); // 2 ลูก d20 ไม่มีตัวไหนถูก drop เลย

        var ex = Assert.Throws<InvalidOperationException>(
            () => AttackResolver.Resolve(roll, targetArmorClass: 10));
        Assert.Contains("d20", ex.Message);
    }

    // --- code review ข้อ 3: HitPointResolver ตอน currentHp=0 อยู่แล้วโดน damage ซ้ำ — ยืนยัน behavior
    // ตรง ๆ ไม่ใช่ปล่อยให้เป็นผลพลอยได้ของสูตร (แม้ตรวจแล้วว่าถูกต้อง) ---

    [Fact]
    public void ApplyDamage_AlreadyAtZeroHp_DamageAtLeastMaxHp_IsInstantDeath()
    {
        // currentHp=0 อยู่แล้ว (สลบ/กำลังทอย death save) โดน damage 20 ขณะ maxHp=20
        var result = HitPointResolver.ApplyDamage(currentHp: 0, maxHp: 20, currentTempHp: 0, damageAmount: 20);

        Assert.Equal(0, result.NewHp);
        Assert.True(result.DroppedToZero);
        Assert.True(result.IsInstantDeath);
    }

    [Fact]
    public void ApplyDamage_AlreadyAtZeroHp_DamageBelowMaxHp_IsNotInstantDeath()
    {
        var result = HitPointResolver.ApplyDamage(currentHp: 0, maxHp: 20, currentTempHp: 0, damageAmount: 19);

        Assert.Equal(0, result.NewHp);
        Assert.True(result.DroppedToZero);
        Assert.False(result.IsInstantDeath);
    }

    [Fact]
    public void ApplyDamage_AlreadyAtZeroHp_TempHpAbsorbsAllDamage_DoesNotReportDroppedToZeroAgain()
    {
        // currentHp=0 อยู่แล้ว แต่มี temp HP ดูดซับ damage ทั้งหมดไว้ก่อนถึง HP จริง —
        // ไม่ควรรายงาน DroppedToZero/IsInstantDeath เพราะไม่มี damage ไหลไปกระทบ HP จริงเลย
        var result = HitPointResolver.ApplyDamage(currentHp: 0, maxHp: 20, currentTempHp: 30, damageAmount: 25);

        Assert.Equal(0, result.NewHp);
        Assert.Equal(5, result.NewTempHp);
        Assert.False(result.DroppedToZero);
        Assert.False(result.IsInstantDeath);
    }
}

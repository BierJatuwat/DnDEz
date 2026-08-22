using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// Attack roll vs AC — นับจาก "d20 ดิบที่ถูกเก็บจริง" (Sides==20 && Dropped==false) ไม่ใช่ total:
/// ดิบ=20 → CriticalHit เสมอ (hit อัตโนมัติไม่สนใจ AC), ดิบ=1 → AutomaticMiss เสมอ
/// (miss อัตโนมัติไม่สนใจ modifier/AC), นอกนั้น hit ถ้า total>=ac
/// </summary>
public class AttackResolverTests
{
    [Fact]
    public void Resolve_Nat20_IsCriticalHitEvenAgainstVeryHighAc()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(20);
        var roll = engine.RollSystem("1d20+0", roller, seed: 1); // total 20

        var result = AttackResolver.Resolve(roll, targetArmorClass: 999);

        Assert.Equal(AttackOutcome.CriticalHit, result.Outcome);
        Assert.True(result.IsHit);
        Assert.Equal(999, result.TargetArmorClass);
    }

    [Fact]
    public void Resolve_Nat1_IsAutomaticMissEvenWithVeryHighModifier()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(1);
        var roll = engine.RollSystem("1d20+50", roller, seed: 1); // total 51

        var result = AttackResolver.Resolve(roll, targetArmorClass: 5);

        Assert.Equal(AttackOutcome.AutomaticMiss, result.Outcome);
        Assert.False(result.IsHit);
    }

    [Fact]
    public void Resolve_NormalRoll_HitsWhenTotalEqualsAc()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10);
        var roll = engine.RollSystem("1d20+5", roller, seed: 1); // total 15

        var result = AttackResolver.Resolve(roll, targetArmorClass: 15);

        Assert.Equal(AttackOutcome.Hit, result.Outcome);
        Assert.True(result.IsHit);
    }

    [Fact]
    public void Resolve_NormalRoll_HitsWhenTotalExceedsAc()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(15);
        var roll = engine.RollSystem("1d20+5", roller, seed: 1); // total 20

        var result = AttackResolver.Resolve(roll, targetArmorClass: 15);

        Assert.Equal(AttackOutcome.Hit, result.Outcome);
    }

    [Fact]
    public void Resolve_NormalRoll_MissesWhenTotalBelowAc()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10);
        var roll = engine.RollSystem("1d20+4", roller, seed: 1); // total 14

        var result = AttackResolver.Resolve(roll, targetArmorClass: 15);

        Assert.Equal(AttackOutcome.Miss, result.Outcome);
        Assert.False(result.IsHit);
    }

    [Fact]
    public void Resolve_Advantage_UsesKeptDieForCritDetection()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(3, 20); // advantage keeps higher (20)
        var roll = engine.RollSystem("1d20+0", roller, seed: 1, advantage: AdvantageMode.Advantage);

        var result = AttackResolver.Resolve(roll, targetArmorClass: 999);

        Assert.Equal(AttackOutcome.CriticalHit, result.Outcome);
    }

    [Fact]
    public void Resolve_Disadvantage_DroppedNat20DoesNotCountAsCrit()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(20, 3); // disadvantage keeps lower (3), drops the 20
        var roll = engine.RollSystem("1d20+0", roller, seed: 1, advantage: AdvantageMode.Disadvantage);

        var result = AttackResolver.Resolve(roll, targetArmorClass: 2);

        // kept die = 3 (total 3 >= ac 2) => Hit ธรรมดา ไม่ใช่ CriticalHit แม้เลข 20 ที่ถูก drop จะโผล่ในผล
        Assert.Equal(AttackOutcome.Hit, result.Outcome);
    }

    [Fact]
    public void Resolve_Disadvantage_KeptNat1CountsAsAutomaticMiss()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(15, 1); // disadvantage keeps lower (1)
        var roll = engine.RollSystem("1d20+50", roller, seed: 1, advantage: AdvantageMode.Disadvantage);

        var result = AttackResolver.Resolve(roll, targetArmorClass: 5);

        Assert.Equal(AttackOutcome.AutomaticMiss, result.Outcome);
    }

    [Fact]
    public void Resolve_RollWithoutD20_ThrowsInvalidOperationException()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(3, 4);
        var roll = engine.RollSystem("2d6", roller, seed: 1);

        Assert.Throws<InvalidOperationException>(() => AttackResolver.Resolve(roll, targetArmorClass: 10));
    }
}

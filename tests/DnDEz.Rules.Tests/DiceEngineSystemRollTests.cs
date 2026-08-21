using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// ทาง SystemRoll ของ DiceEngine — ใช้ SequenceDiceRoller (fake) เพื่อคุมผลทอยตายตัว
/// ทดสอบ logic ล้วน ๆ (sum/sign/kh-kl/advantage) แยกจาก algorithm ของ RNG จริง
/// </summary>
public class DiceEngineSystemRollTests
{
    [Fact]
    public void RollSystem_SingleD20_ReturnsRolledValueAsTotal()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(15);

        var result = engine.RollSystem("1d20", roller, seed: 1);

        Assert.Equal(15, result.Total);
        var detail = Assert.Single(result.Rolls);
        Assert.Equal(20, detail.Sides);
        Assert.Equal(15, detail.Value);
        Assert.False(detail.Dropped);
        Assert.Equal(1, detail.Sign);
    }

    [Fact]
    public void RollSystem_WithFlatModifier_AddsToTotal()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(4, 5);

        var result = engine.RollSystem("2d6+3", roller, seed: 1);

        Assert.Equal(12, result.Total); // 4+5+3
        Assert.Equal(new[] { 6, 6 }, roller.RequestedSides);
    }

    [Fact]
    public void RollSystem_MultipleGroups_RequestsCorrectSidesInOrder()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10, 2, 3);

        var result = engine.RollSystem("1d20+2d4+3", roller, seed: 1);

        Assert.Equal(18, result.Total); // 10+2+3+3
        Assert.Equal(new[] { 20, 4, 4 }, roller.RequestedSides);
    }

    [Fact]
    public void RollSystem_NegativeGroupSign_SubtractsGroupSum()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(15, 3, 4);

        var result = engine.RollSystem("1d20-2d4", roller, seed: 1);

        Assert.Equal(8, result.Total); // 15 - (3+4)
    }

    [Fact]
    public void RollSystem_KeepHighest_DropsLowestAndSumsKept()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(2, 6, 4, 1);

        var result = engine.RollSystem("4d6kh3", roller, seed: 1);

        Assert.Equal(12, result.Total); // keep 6+4+2, drop 1
        Assert.Equal(4, result.Rolls.Count);

        var dropped = Assert.Single(result.Rolls, r => r.Dropped);
        Assert.Equal(1, dropped.Value);

        var keptValues = result.Rolls.Where(r => !r.Dropped).Select(r => r.Value).OrderByDescending(v => v);
        Assert.Equal(new[] { 6, 4, 2 }, keptValues);
    }

    [Fact]
    public void RollSystem_KeepLowest_DropsHighestAndSumsKept()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(2, 6, 4, 1);

        var result = engine.RollSystem("4d6kl1", roller, seed: 1);

        Assert.Equal(1, result.Total); // keep only the lowest value
        var kept = Assert.Single(result.Rolls, r => !r.Dropped);
        Assert.Equal(1, kept.Value);
    }

    [Fact]
    public void RollSystem_Advantage_RollsTwiceAndKeepsHigher()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(8, 17);

        var result = engine.RollSystem("1d20", roller, seed: 1, advantage: AdvantageMode.Advantage);

        Assert.Equal(17, result.Total);
        Assert.Equal(2, result.Rolls.Count);
        Assert.Equal(new[] { 20, 20 }, roller.RequestedSides);

        var dropped = Assert.Single(result.Rolls, r => r.Dropped);
        Assert.Equal(8, dropped.Value);
        var kept = Assert.Single(result.Rolls, r => !r.Dropped);
        Assert.Equal(17, kept.Value);
        Assert.Equal(AdvantageMode.Advantage, result.Advantage);
    }

    [Fact]
    public void RollSystem_Disadvantage_RollsTwiceAndKeepsLower()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(8, 17);

        var result = engine.RollSystem("1d20", roller, seed: 1, advantage: AdvantageMode.Disadvantage);

        Assert.Equal(8, result.Total);
        var dropped = Assert.Single(result.Rolls, r => r.Dropped);
        Assert.Equal(17, dropped.Value);
    }

    [Fact]
    public void RollSystem_AdvantageWithModifier_KeepsHigherThenAddsModifier()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(3, 19);

        var result = engine.RollSystem("1d20+5", roller, seed: 1, advantage: AdvantageMode.Advantage);

        Assert.Equal(24, result.Total); // 19 + 5
    }

    [Theory]
    [InlineData("2d20")]      // ไม่ใช่ d20 เดี่ยว
    [InlineData("4d6")]       // ไม่ใช่ d20 เลย
    [InlineData("2d20kh1")]   // มี Keep อยู่แล้ว ไม่ใช่ base d20 เปล่า ๆ
    public void RollSystem_AdvantageOnNonBaseD20_Throws(string expression)
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(); // ไม่ควรถูกเรียกเลยถ้า validate ก่อนทอย

        Assert.Throws<DiceExpressionException>(
            () => engine.RollSystem(expression, roller, seed: 1, advantage: AdvantageMode.Advantage));
    }

    [Fact]
    public void RollSystem_ResultSource_IsSystemRoll()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10);

        var result = engine.RollSystem("1d20", roller, seed: 999);

        Assert.Equal(DiceSource.SystemRoll, result.Source);
    }

    [Fact]
    public void RollSystem_SeedPassedThrough_IsStoredOnResult()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10);

        var result = engine.RollSystem("1d20", roller, seed: 555555);

        Assert.Equal(555555, result.Seed);
    }

    [Fact]
    public void RollSystem_ExpressionEchoedBackOnResult()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10, 2, 3);

        var result = engine.RollSystem("1d20+2d4+3", roller, seed: 1);

        Assert.Equal("1d20+2d4+3", result.Expression);
    }
}

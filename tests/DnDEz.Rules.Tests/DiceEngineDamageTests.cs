using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// DiceEngine.RollDamage — crit ดับเบิลจำนวนลูกเต๋าต่อกลุ่ม (Count) ไม่ใช่ผลรวม ไม่แตะ FlatModifier
/// เลย ยืนยันด้วยตัวอย่างเลขจริงตามที่บรีฟกำหนด
/// </summary>
public class DiceEngineDamageTests
{
    [Fact]
    public void RollDamage_NonCritical_RollsExactCountRequested()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(5);

        var result = engine.RollDamage("1d8+3", roller, isCritical: false, seed: 1);

        Assert.Equal(new[] { 8 }, roller.RequestedSides);
        Assert.Equal(8, result.Total); // 5 + 3
        Assert.Equal(DiceSource.SystemRoll, result.Source);
        Assert.Equal(1, result.Seed);
    }

    [Fact]
    public void RollDamage_Critical_DoublesDiceCountButNotFlatModifier()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(5, 6);

        var result = engine.RollDamage("1d8+3", roller, isCritical: true, seed: 1);

        Assert.Equal(new[] { 8, 8 }, roller.RequestedSides); // ทอย d8 สองลูกแทนที่จะเป็นลูกเดียว
        Assert.Equal(14, result.Total); // 5+6+3 (modifier บวกครั้งเดียว ไม่ถูกดับเบิล)
        Assert.Equal(2, result.Rolls.Count);
    }

    [Fact]
    public void RollDamage_Critical_DoublesEachGroupIndependently()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(1, 2, 3, 4, 5, 6);

        var result = engine.RollDamage("1d6+2d4+1", roller, isCritical: true, seed: 1);

        Assert.Equal(new[] { 6, 6, 4, 4, 4, 4 }, roller.RequestedSides); // 2d6 + 4d4
        Assert.Equal(22, result.Total); // (1+2) + (3+4+5+6) + 1
    }

    [Fact]
    public void RollDamage_Critical_PreservesNegativeGroupSign()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(5, 6, 1, 2);

        var result = engine.RollDamage("1d8-1d4+2", roller, isCritical: true, seed: 1);

        Assert.Equal(new[] { 8, 8, 4, 4 }, roller.RequestedSides);
        Assert.Equal(10, result.Total); // (5+6) - (1+2) + 2
    }

    [Fact]
    public void RollDamage_Critical_WithKeepHighest_DoesNotDoubleKeepAmount()
    {
        var engine = new DiceEngine();
        // 4d6kh3 doubled -> 8d6kh3 (Count ดับเบิลเป็น 8, Keep.Amount ยังคงเป็น 3 ไม่ถูกดับเบิลเป็น 6)
        var roller = new SequenceDiceRoller(1, 2, 3, 4, 5, 6, 7, 8);

        var result = engine.RollDamage("4d6kh3", roller, isCritical: true, seed: 1);

        Assert.Equal(8, roller.RequestedSides.Count); // ทอย 8 ลูก ไม่ใช่ 6
        Assert.All(roller.RequestedSides, sides => Assert.Equal(6, sides));
        Assert.Equal(21, result.Total); // เก็บสูงสุด 3 จาก 8 ลูก = 6+7+8
        Assert.Equal(3, result.Rolls.Count(r => !r.Dropped));
        Assert.Equal(5, result.Rolls.Count(r => r.Dropped));
    }

    [Fact]
    public void RollDamage_MultipleComponents_ExpressionEchoedBack()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(4);

        var result = engine.RollDamage("1d6", roller, isCritical: false, seed: 42);

        Assert.Equal("1d6", result.Expression);
        Assert.Equal(4, result.Total);
    }
}

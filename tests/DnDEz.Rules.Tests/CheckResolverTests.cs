using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// Ability check / saving throw vs DC — success ถ้า Total >= Dc เท่านั้น ไม่มี auto-success/auto-fail
/// จาก nat20/nat1 (ต่างจาก attack roll โดยเฉพาะ — ดู AttackResolverTests)
/// </summary>
public class CheckResolverTests
{
    [Fact]
    public void Resolve_TotalEqualsDc_Succeeds()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10);
        var roll = engine.RollSystem("1d20+5", roller, seed: 1); // total 15

        var result = CheckResolver.Resolve(roll, dc: 15);

        Assert.True(result.Success);
        Assert.Equal(15, result.Dc);
        Assert.Same(roll, result.Roll);
    }

    [Fact]
    public void Resolve_TotalAboveDc_Succeeds()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(15);
        var roll = engine.RollSystem("1d20+5", roller, seed: 1); // total 20

        var result = CheckResolver.Resolve(roll, dc: 15);

        Assert.True(result.Success);
    }

    [Fact]
    public void Resolve_TotalBelowDc_Fails()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(10);
        var roll = engine.RollSystem("1d20+4", roller, seed: 1); // total 14

        var result = CheckResolver.Resolve(roll, dc: 15);

        Assert.False(result.Success);
    }

    [Fact]
    public void Resolve_Nat20_DoesNotAutoSucceedWhenTotalBelowDc()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(20);
        var roll = engine.RollSystem("1d20-15", roller, seed: 1); // d20=20 ดิบ แต่ total=5

        var result = CheckResolver.Resolve(roll, dc: 10);

        Assert.False(result.Success); // check/save ไม่มี auto-success จาก nat20
    }

    [Fact]
    public void Resolve_Nat1_DoesNotAutoFailWhenTotalMeetsDc()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(1);
        var roll = engine.RollSystem("1d20+15", roller, seed: 1); // d20=1 ดิบ แต่ total=16

        var result = CheckResolver.Resolve(roll, dc: 10);

        Assert.True(result.Success); // check/save ไม่มี auto-fail จาก nat1
    }

    [Fact]
    public void Resolve_WorksWithAdvantageRoll()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(8, 17);
        var roll = engine.RollSystem("1d20+2", roller, seed: 1, advantage: AdvantageMode.Advantage); // keep 17 -> total 19

        var result = CheckResolver.Resolve(roll, dc: 19);

        Assert.True(result.Success);
    }

    [Fact]
    public void Resolve_WorksWithDisadvantageRoll()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(17, 8);
        var roll = engine.RollSystem("1d20+2", roller, seed: 1, advantage: AdvantageMode.Disadvantage); // keep 8 -> total 10

        var result = CheckResolver.Resolve(roll, dc: 15);

        Assert.False(result.Success);
    }
}

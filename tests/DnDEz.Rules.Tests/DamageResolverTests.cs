using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// DamageResolver.RollAndResolve — ทอย damage หลาย component (คนละ damage type ได้) แล้วหัก
/// resistance/vulnerability/immunity ต่อ component ก่อนรวมเป็นยอดสุดท้าย — integration ระหว่าง
/// DiceEngine.RollDamage กับ DamageTypeResolver
/// </summary>
public class DamageResolverTests
{
    private static readonly IReadOnlySet<DamageType> Empty = new HashSet<DamageType>();

    [Fact]
    public void RollAndResolve_SingleComponent_NoResistance_TotalEqualsRaw()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(5); // 1d8+3 = 8
        var components = new[] { new DamageComponent("1d8+3", DamageType.Slashing) };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, Empty, Empty, Empty, seed: 1);

        var component = Assert.Single(result.Components);
        Assert.Equal(DamageType.Slashing, component.Type);
        Assert.Equal(8, component.RawAmount);
        Assert.Equal(8, component.AdjustedAmount);
        Assert.Equal(8, result.TotalAdjustedAmount);
    }

    [Fact]
    public void RollAndResolve_MultipleComponents_DifferentTypes_AppliesResistancePerComponent()
    {
        var engine = new DiceEngine();
        // component 1: "1d6" slashing -> 4 (no resist)
        // component 2: "2d6" fire -> 3+5=8, resisted -> floor(8/2)=4
        var roller = new SequenceDiceRoller(4, 3, 5);
        var components = new[]
        {
            new DamageComponent("1d6", DamageType.Slashing),
            new DamageComponent("2d6", DamageType.Fire)
        };
        var resistances = new HashSet<DamageType> { DamageType.Fire };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, resistances, Empty, Empty, seed: 1);

        Assert.Equal(2, result.Components.Count);
        Assert.Equal(DamageType.Slashing, result.Components[0].Type);
        Assert.Equal(4, result.Components[0].RawAmount);
        Assert.Equal(4, result.Components[0].AdjustedAmount);

        Assert.Equal(DamageType.Fire, result.Components[1].Type);
        Assert.Equal(8, result.Components[1].RawAmount);
        Assert.Equal(4, result.Components[1].AdjustedAmount);

        Assert.Equal(8, result.TotalAdjustedAmount); // 4 + 4
    }

    [Fact]
    public void RollAndResolve_CriticalHit_DoublesDiceForEachComponentBeforeResistance()
    {
        var engine = new DiceEngine();
        // 1d8 fire crit -> 2d8 -> 6+2=8 raw, resisted -> floor(8/2)=4
        var roller = new SequenceDiceRoller(6, 2);
        var components = new[] { new DamageComponent("1d8", DamageType.Fire) };
        var resistances = new HashSet<DamageType> { DamageType.Fire };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: true, resistances, Empty, Empty, seed: 1);

        var component = Assert.Single(result.Components);
        Assert.Equal(2, component.Roll.Rolls.Count); // ยืนยันว่า crit ดับเบิลลูกเต๋าจริง
        Assert.Equal(8, component.RawAmount);
        Assert.Equal(4, component.AdjustedAmount);
        Assert.Equal(4, result.TotalAdjustedAmount);
    }

    [Fact]
    public void RollAndResolve_ImmuneComponent_ZeroedButOtherComponentsUnaffected()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(6, 4); // fire=6 (immune->0), cold=4 (no profile)
        var components = new[]
        {
            new DamageComponent("1d6", DamageType.Fire),
            new DamageComponent("1d6", DamageType.Cold)
        };
        var immunities = new HashSet<DamageType> { DamageType.Fire };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, Empty, Empty, immunities, seed: 1);

        Assert.Equal(0, result.Components[0].AdjustedAmount);
        Assert.Equal(6, result.Components[0].RawAmount); // raw ยังบันทึกไว้ตามจริง แม้ adjusted เป็น 0
        Assert.Equal(4, result.Components[1].AdjustedAmount);
        Assert.Equal(4, result.TotalAdjustedAmount);
    }

    [Fact]
    public void RollAndResolve_VulnerableComponent_Doubles()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(5);
        var components = new[] { new DamageComponent("1d8", DamageType.Radiant) };
        var vulnerabilities = new HashSet<DamageType> { DamageType.Radiant };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, Empty, vulnerabilities, Empty, seed: 1);

        Assert.Equal(5, result.Components[0].RawAmount);
        Assert.Equal(10, result.Components[0].AdjustedAmount);
        Assert.Equal(10, result.TotalAdjustedAmount);
    }

    [Fact]
    public void RollAndResolve_ComponentOrderIsPreservedInResult()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(1, 1, 1);
        var components = new[]
        {
            new DamageComponent("1d4", DamageType.Fire),
            new DamageComponent("1d4", DamageType.Cold),
            new DamageComponent("1d4", DamageType.Poison)
        };

        var result = DamageResolver.RollAndResolve(
            engine, components, roller, isCritical: false, Empty, Empty, Empty, seed: 1);

        Assert.Equal(
            new[] { DamageType.Fire, DamageType.Cold, DamageType.Poison },
            result.Components.Select(c => c.Type));
    }
}

namespace DnDEz.Rules.Tests;

/// <summary>
/// Pure math ของการซ้อน resistance/vulnerability/immunity ต่อ damage type เดียว — แยกทดสอบจากการ
/// ทอยเต๋าเพราะเป็นคนละ concern (ดู DamageResolverTests สำหรับ integration กับการทอยจริง)
/// </summary>
public class DamageTypeResolverTests
{
    private static readonly IReadOnlySet<DamageType> Empty = new HashSet<DamageType>();

    [Fact]
    public void Resolve_NoProfile_ReturnsRawAmountUnchanged()
    {
        var result = DamageTypeResolver.Resolve(DamageType.Fire, 10, Empty, Empty, Empty);

        Assert.Equal(10, result);
    }

    [Fact]
    public void Resolve_Immune_ReturnsZero()
    {
        var immune = new HashSet<DamageType> { DamageType.Fire };

        var result = DamageTypeResolver.Resolve(DamageType.Fire, 10, Empty, Empty, immune);

        Assert.Equal(0, result);
    }

    [Fact]
    public void Resolve_ImmuneWinsEvenWhenAlsoResistantAndVulnerable()
    {
        var all = new HashSet<DamageType> { DamageType.Fire };

        var result = DamageTypeResolver.Resolve(DamageType.Fire, 10, resistances: all, vulnerabilities: all, immunities: all);

        Assert.Equal(0, result); // immune ชนะทุกกรณี
    }

    [Fact]
    public void Resolve_ResistantOnly_HalvesRoundedDown()
    {
        var resist = new HashSet<DamageType> { DamageType.Cold };

        var result = DamageTypeResolver.Resolve(DamageType.Cold, 7, resist, Empty, Empty);

        Assert.Equal(3, result); // floor(7/2) = 3, ไม่ใช่ 3.5 หรือ 4
    }

    [Fact]
    public void Resolve_ResistantOnly_EvenAmount_HalvesExactly()
    {
        var resist = new HashSet<DamageType> { DamageType.Cold };

        var result = DamageTypeResolver.Resolve(DamageType.Cold, 10, resist, Empty, Empty);

        Assert.Equal(5, result);
    }

    [Fact]
    public void Resolve_VulnerableOnly_Doubles()
    {
        var vuln = new HashSet<DamageType> { DamageType.Radiant };

        var result = DamageTypeResolver.Resolve(DamageType.Radiant, 6, Empty, vuln, Empty);

        Assert.Equal(12, result);
    }

    [Fact]
    public void Resolve_ResistantAndVulnerable_CancelOutToRawAmount_NotFloorThenDouble()
    {
        var both = new HashSet<DamageType> { DamageType.Necrotic };

        var result = DamageTypeResolver.Resolve(DamageType.Necrotic, 7, resistances: both, vulnerabilities: both, immunities: Empty);

        // ต้องหักล้างกันเป็น damage ดิบ (7) ตามนิยาม RAW ตรง ๆ — ไม่ใช่ floor(7/2)*2 = 6
        Assert.Equal(7, result);
    }

    [Fact]
    public void Resolve_ProfileForDifferentDamageType_DoesNotApply()
    {
        var resist = new HashSet<DamageType> { DamageType.Fire };

        var result = DamageTypeResolver.Resolve(DamageType.Cold, 10, resist, Empty, Empty);

        Assert.Equal(10, result); // resist ต่อ Fire ไม่มีผลกับ Cold
    }

    [Fact]
    public void Resolve_ZeroRawAmount_StaysZero()
    {
        var resist = new HashSet<DamageType> { DamageType.Poison };

        var result = DamageTypeResolver.Resolve(DamageType.Poison, 0, resist, Empty, Empty);

        Assert.Equal(0, result);
    }

    [Fact]
    public void Resolve_MultipleUnrelatedTypesInSets_OnlyMatchingTypeApplies()
    {
        var resist = new HashSet<DamageType> { DamageType.Fire, DamageType.Cold, DamageType.Poison };
        var vuln = new HashSet<DamageType> { DamageType.Radiant };

        var fireResult = DamageTypeResolver.Resolve(DamageType.Fire, 8, resist, vuln, Empty);
        var radiantResult = DamageTypeResolver.Resolve(DamageType.Radiant, 8, resist, vuln, Empty);
        var slashingResult = DamageTypeResolver.Resolve(DamageType.Slashing, 8, resist, vuln, Empty);

        Assert.Equal(4, fireResult);     // resist only
        Assert.Equal(16, radiantResult); // vulnerable only
        Assert.Equal(8, slashingResult); // neither
    }
}

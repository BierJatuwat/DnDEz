namespace DnDEz.Rules.Tests;

/// <summary>advantage + disadvantage พร้อมกันต้องหักล้างกัน (กติกา D&D 2024) — ทอยปกติ ไม่ใช่ advantage ซ้อน</summary>
public class AdvantageResolverTests
{
    [Fact]
    public void Resolve_Neither_ReturnsNone()
        => Assert.Equal(AdvantageMode.None, AdvantageResolver.Resolve(false, false));

    [Fact]
    public void Resolve_AdvantageOnly_ReturnsAdvantage()
        => Assert.Equal(AdvantageMode.Advantage, AdvantageResolver.Resolve(true, false));

    [Fact]
    public void Resolve_DisadvantageOnly_ReturnsDisadvantage()
        => Assert.Equal(AdvantageMode.Disadvantage, AdvantageResolver.Resolve(false, true));

    [Fact]
    public void Resolve_Both_CancelOutToNone()
        => Assert.Equal(AdvantageMode.None, AdvantageResolver.Resolve(true, true));
}

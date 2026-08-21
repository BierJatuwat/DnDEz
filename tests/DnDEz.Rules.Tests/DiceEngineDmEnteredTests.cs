namespace DnDEz.Rules.Tests;

/// <summary>
/// ทาง DmEntered — DM กรอกเลขดิบจากเต๋าจริงบนโต๊ะ engine คำนวณ total/kh-kl/modifier เหมือนทาง
/// SystemRoll ทุกอย่าง ต่างแค่ที่มาของตัวเลข (player-side-design §5) — ต้องไม่มี seed เกิดขึ้นเลย
/// (การเช็คว่า "ใคร" มีสิทธิ์กรอก เป็นหน้าที่ authorization ชั้นบน engine รู้แค่ว่ารับเลขดิบมาคำนวณ)
/// </summary>
public class DiceEngineDmEnteredTests
{
    [Fact]
    public void RollFromDmEntered_BasicExpression_ComputesTotalFromRawValues()
    {
        var engine = new DiceEngine();

        var result = engine.RollFromDmEntered("2d6+3", rawValues: new[] { 4, 5 });

        Assert.Equal(12, result.Total); // 4+5+3
        Assert.Equal(DiceSource.DmEntered, result.Source);
    }

    [Fact]
    public void RollFromDmEntered_Result_NeverHasSeed()
    {
        var engine = new DiceEngine();

        var result = engine.RollFromDmEntered("1d20", rawValues: new[] { 14 });

        Assert.Null(result.Seed);
    }

    [Fact]
    public void RollFromDmEntered_TooFewRawValues_Throws()
    {
        var engine = new DiceEngine();

        Assert.Throws<DiceExpressionException>(
            () => engine.RollFromDmEntered("2d6", rawValues: new[] { 4 }));
    }

    [Fact]
    public void RollFromDmEntered_TooManyRawValues_Throws()
    {
        var engine = new DiceEngine();

        Assert.Throws<DiceExpressionException>(
            () => engine.RollFromDmEntered("2d6", rawValues: new[] { 4, 5, 6 }));
    }

    [Theory]
    [InlineData(25)] // เกิน 20
    [InlineData(0)]  // ต่ำกว่า 1
    [InlineData(-3)] // ติดลบ — กรอกผิดหรือพยายามโกงให้ total ต่ำ/สูงผิดปกติ
    public void RollFromDmEntered_RawValueOutOfRange_Throws(int badValue)
    {
        var engine = new DiceEngine();

        Assert.Throws<DiceExpressionException>(
            () => engine.RollFromDmEntered("1d20", rawValues: new[] { badValue }));
    }

    [Fact]
    public void RollFromDmEntered_KeepHighest_ComputesSameAsSystemRoll()
    {
        var engine = new DiceEngine();

        var result = engine.RollFromDmEntered("4d6kh3", rawValues: new[] { 2, 6, 4, 1 });

        Assert.Equal(12, result.Total); // keep 6+4+2, drop 1
        Assert.Equal(4, result.Rolls.Count);
        var dropped = Assert.Single(result.Rolls, r => r.Dropped);
        Assert.Equal(1, dropped.Value);
    }

    [Fact]
    public void RollFromDmEntered_Advantage_RequiresTwoRawValuesAndKeepsHigher()
    {
        var engine = new DiceEngine();

        var result = engine.RollFromDmEntered(
            "1d20", rawValues: new[] { 8, 17 }, advantage: AdvantageMode.Advantage);

        Assert.Equal(17, result.Total);
        var dropped = Assert.Single(result.Rolls, r => r.Dropped);
        Assert.Equal(8, dropped.Value);
    }

    [Fact]
    public void RollFromDmEntered_AdvantageButOnlyOneRawValue_Throws()
    {
        var engine = new DiceEngine();

        Assert.Throws<DiceExpressionException>(
            () => engine.RollFromDmEntered("1d20", rawValues: new[] { 17 }, advantage: AdvantageMode.Advantage));
    }

    [Fact]
    public void RollFromDmEntered_NegativeGroupSign_SubtractsFromTotal()
    {
        var engine = new DiceEngine();

        var result = engine.RollFromDmEntered("1d20-2d4", rawValues: new[] { 15, 3, 4 });

        Assert.Equal(8, result.Total); // 15 - (3+4)
    }

    [Fact]
    public void RollFromDmEntered_NullRawValues_Throws()
    {
        var engine = new DiceEngine();

        Assert.Throws<DiceExpressionException>(
            () => engine.RollFromDmEntered("1d20", rawValues: null!));
    }
}

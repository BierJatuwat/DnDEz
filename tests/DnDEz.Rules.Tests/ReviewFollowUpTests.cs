using DnDEz.Rules;
using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// ปิดช่องว่างที่ code review + security review ชี้ไว้หลัง Phase 1 green
/// (แยกไฟล์ต่างหากเพื่อไม่แตะเทสต์ชุดเดิมที่ owner เขียน)
/// </summary>
public class ReviewFollowUpTests
{
    // --- code review: branch advantage/disadvantage ที่ผลเสมอกัน ไม่มีเทสต์ยืนยัน exact behaviour ---

    [Fact]
    public void RollSystem_AdvantageWithTiedRolls_KeepsFirstDropsSecond()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(12, 12);

        var result = engine.RollSystem("1d20+3", roller, seed: 1, advantage: AdvantageMode.Advantage);

        Assert.Equal(15, result.Total); // 12 + 3
        Assert.Equal(2, result.Rolls.Count);
        Assert.False(result.Rolls[0].Dropped);
        Assert.True(result.Rolls[1].Dropped);
    }

    [Fact]
    public void RollSystem_DisadvantageWithTiedRolls_KeepsFirstDropsSecond()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(7, 7);

        var result = engine.RollSystem("1d20", roller, seed: 1, advantage: AdvantageMode.Disadvantage);

        Assert.Equal(7, result.Total);
        Assert.False(result.Rolls[0].Dropped);
        Assert.True(result.Rolls[1].Dropped);
    }

    [Fact]
    public void RollSystem_TiedRolls_ExactlyOneDieIsKept()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(20, 20);

        var result = engine.RollSystem("1d20", roller, seed: 1, advantage: AdvantageMode.Advantage);

        // กันกรณี refactor แล้วเผลอ mark dropped ทั้งคู่ หรือไม่ mark เลย
        Assert.Single(result.Rolls, r => !r.Dropped);
    }

    // --- code review: expression ที่ผ่าน regex ได้แต่ไม่มีกลุ่มลูกเต๋าเลย (logic path คนละจุดกับ syntax error) ---

    [Theory]
    [InlineData("5")]
    [InlineData("5+3")]
    [InlineData("10-2")]
    public void Parse_PureModifierWithoutDiceGroup_Throws(string expression)
    {
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    // --- security review: .NET `\d` จับ Unicode digit ด้วย แต่ long.TryParse ไม่รับ ---
    // พฤติกรรมเดิม reject อยู่แล้ว (fail closed) แต่ข้อความ error บอกว่า "ค่าเกินขอบเขต"
    // ซึ่งไม่ตรงความจริง — ที่ถูกคือ syntax ไม่ถูกต้องเพราะไม่ใช่เลขอารบิก 0-9

    [Theory]
    [InlineData("๑d๒๐")]      // เลขไทย
    [InlineData("١d٢٠")]      // เลขอารบิก-อินดิก
    [InlineData("１d２０")]     // เลข fullwidth
    [InlineData("๑๐d6")]
    [InlineData("1d20+๕")]
    public void Parse_NonAsciiDigits_ThrowsWithSyntaxMessage(string expression)
    {
        var ex = Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));

        Assert.Contains("syntax", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("เกินขอบเขต", ex.Message);
    }

    [Fact]
    public void Parse_AsciiDigitsStillWork_AfterUnicodeTightening()
    {
        var parsed = DiceExpressionParser.Parse("4d6kh3+2");

        Assert.Single(parsed.Groups);
        Assert.Equal(4, parsed.Groups[0].Count);
        Assert.Equal(6, parsed.Groups[0].Sides);
        Assert.Equal(2, parsed.FlatModifier);
    }
}

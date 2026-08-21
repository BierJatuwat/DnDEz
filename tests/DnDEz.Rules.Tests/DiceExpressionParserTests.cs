namespace DnDEz.Rules.Tests;

/// <summary>
/// สเปกของ notation ที่ parser ต้องรองรับ: "XdY" ต้องมีจำนวนลูก X ระบุชัดเสมอ (ไม่มี default แบบ "d20")
/// ต้องรองรับ +/-, หลาย term, kh/kl, case-insensitive, whitespace — และมี guard rail กัน DoS
/// จาก input พิสดาร (จำนวนลูก/ขนาดลูก/จำนวนกลุ่มเกินเพดาน, ตัวเลขล้น, string ยาวเกิน)
/// </summary>
public class DiceExpressionParserTests
{
    // ── Happy path ──────────────────────────────────────────────

    [Fact]
    public void Parse_SingleD20_ReturnsOneGroupNoModifier()
    {
        var result = DiceExpressionParser.Parse("1d20");

        var group = Assert.Single(result.Groups);
        Assert.Equal(1, group.Count);
        Assert.Equal(20, group.Sides);
        Assert.Equal(1, group.Sign);
        Assert.Null(group.Keep);
        Assert.Equal(0, result.FlatModifier);
    }

    [Fact]
    public void Parse_WithPositiveModifier_ReturnsFlatModifier()
    {
        var result = DiceExpressionParser.Parse("2d6+3");

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Count);
        Assert.Equal(6, group.Sides);
        Assert.Equal(3, result.FlatModifier);
    }

    [Fact]
    public void Parse_WithNegativeModifier_ReturnsNegativeFlatModifier()
    {
        var result = DiceExpressionParser.Parse("1d20-5");

        Assert.Equal(-5, result.FlatModifier);
    }

    [Fact]
    public void Parse_MultipleTerms_ReturnsAllGroupsAndSummedModifier()
    {
        var result = DiceExpressionParser.Parse("2d6+1d4+3");

        Assert.Equal(2, result.Groups.Count);

        Assert.Equal(2, result.Groups[0].Count);
        Assert.Equal(6, result.Groups[0].Sides);
        Assert.Equal(1, result.Groups[0].Sign);

        Assert.Equal(1, result.Groups[1].Count);
        Assert.Equal(4, result.Groups[1].Sides);
        Assert.Equal(1, result.Groups[1].Sign);

        Assert.Equal(3, result.FlatModifier);
    }

    [Fact]
    public void Parse_MultipleModifierTerms_SumsIntoOneFlatModifier()
    {
        // "1d20+3-1" -> flat modifier รวมกันเป็น 2
        var result = DiceExpressionParser.Parse("1d20+3-1");

        Assert.Equal(2, result.FlatModifier);
    }

    [Fact]
    public void Parse_NegativeGroupAfterPositiveTerm_AppliesNegativeSignToGroup()
    {
        // "1d20-2d4" -> group ที่สองมี sign = -1 (ไม่ใช่ modifier)
        var result = DiceExpressionParser.Parse("1d20-2d4");

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(1, result.Groups[0].Sign);
        Assert.Equal(-1, result.Groups[1].Sign);
        Assert.Equal(2, result.Groups[1].Count);
        Assert.Equal(4, result.Groups[1].Sides);
        Assert.Equal(0, result.FlatModifier);
    }

    [Theory]
    [InlineData("4d6kh3", 4, 6, KeepKind.Highest, 3)]
    [InlineData("2d20kl1", 2, 20, KeepKind.Lowest, 1)]
    public void Parse_KeepHighestOrLowest_ReturnsKeepSpec(
        string expression, int count, int sides, KeepKind kind, int amount)
    {
        var result = DiceExpressionParser.Parse(expression);

        var group = Assert.Single(result.Groups);
        Assert.Equal(count, group.Count);
        Assert.Equal(sides, group.Sides);
        Assert.NotNull(group.Keep);
        Assert.Equal(kind, group.Keep!.Kind);
        Assert.Equal(amount, group.Keep.Amount);
    }

    [Theory]
    [InlineData("1D20")]
    [InlineData("1d20")]
    [InlineData("4D6KH3")]
    [InlineData("4d6Kh3")]
    public void Parse_IsCaseInsensitive(string expression)
    {
        var result = DiceExpressionParser.Parse(expression);
        Assert.NotEmpty(result.Groups);
    }

    [Theory]
    [InlineData(" 1d20 + 3 ")]
    [InlineData("1d20\t+\t3")]
    [InlineData("  2d6  +  1d4  +  3  ")]
    public void Parse_IgnoresExtraWhitespace(string expression)
    {
        var result = DiceExpressionParser.Parse(expression);
        Assert.NotEmpty(result.Groups);
    }

    // ── Guard rail / malformed input ────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_Throws(string? expression)
    {
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression!));
    }

    [Fact]
    public void Parse_MissingExplicitCount_Throws()
    {
        // สเปกบังคับ "XdY" ต้องมี X เสมอ ไม่มี default "d20" = "1d20"
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse("d20"));
    }

    [Theory]
    [InlineData("0d6")]      // count = 0
    [InlineData("-1d6")]     // ห้ามมี sign นำหน้า term แรกของทั้ง expression
    [InlineData("-3d6")]
    public void Parse_NonPositiveOrLeadingSignedCount_Throws(string expression)
    {
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    [Theory]
    [InlineData("1d0")]
    [InlineData("1d1")]
    public void Parse_SidesBelowMinimum_Throws(string expression)
    {
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    [Fact]
    public void Parse_SidesAboveMaximum_Throws()
    {
        var expression = $"1d{DiceExpressionParser.MaxSides + 1}";
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    [Fact]
    public void Parse_CountAboveMaximum_ThrowsAsDosGuard()
    {
        // "999999999d20" ต้อง reject ด้วย guard rail ไม่ใช่พยายามทอยจริง (DoS)
        var expression = $"{DiceExpressionParser.MaxDiceCountPerGroup + 1}d20";
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse("999999999d20"));
    }

    [Fact]
    public void Parse_TooManyGroups_Throws()
    {
        var expression = string.Join("+",
            Enumerable.Repeat("1d6", DiceExpressionParser.MaxGroupsPerExpression + 1));

        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    [Theory]
    [InlineData("4d6kh5")]  // keep เกินจำนวนลูกที่มี
    [InlineData("4d6kh0")]  // keep = 0
    [InlineData("4d6kl0")]
    public void Parse_KeepAmountOutOfRange_Throws(string expression)
    {
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1d")]
    [InlineData("d")]
    [InlineData("1d20++3")]
    [InlineData("1d20 kh")]
    [InlineData("kh3")]
    [InlineData("1d20*2")]
    [InlineData("1d20#3")]
    public void Parse_MalformedSyntax_Throws(string expression)
    {
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    [Fact]
    public void Parse_IntegerOverflowInCount_ThrowsInsteadOfCrashing()
    {
        // เกิน int.MaxValue แต่ยังพอดี long — ต้องไม่ปล่อย OverflowException ดิบออกมา
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse("999999999999999d20"));
    }

    [Fact]
    public void Parse_NumberOverflowingEvenLong_ThrowsInsteadOfCrashing()
    {
        // เกิน long.MaxValue ด้วยซ้ำ — ต้อง handle โดยไม่ throw OverflowException/FormatException ดิบ
        Assert.Throws<DiceExpressionException>(
            () => DiceExpressionParser.Parse("999999999999999999999999999999999d20"));
    }

    [Fact]
    public void Parse_ExpressionLongerThanMax_Throws()
    {
        var expression = new string(' ', DiceExpressionParser.MaxExpressionLength + 50) + "1d6";
        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));
    }

    [Fact]
    public void Parse_HugeDigitString_DoesNotHangAndThrowsQuickly()
    {
        // ป้องกัน DoS จาก string ตัวเลขมหาศาล — ต้อง fail เร็ว ไม่ค้าง ไม่ OOM
        var expression = new string('9', 5000) + "d20";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Throws<DiceExpressionException>(() => DiceExpressionParser.Parse(expression));

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"Parse ใช้เวลา {stopwatch.ElapsedMilliseconds}ms — สงสัยว่ามี regex/loop ที่ blow up ตาม input");
    }
}

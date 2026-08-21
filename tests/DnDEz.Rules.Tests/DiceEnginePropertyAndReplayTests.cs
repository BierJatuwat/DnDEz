namespace DnDEz.Rules.Tests;

/// <summary>
/// Property-style: ผลลัพธ์ต้องอยู่ในช่วงที่เป็นไปได้เสมอ ไม่ว่า seed จะเป็นอะไร
/// + เทสต์ replay ระดับ engine (ไม่ใช่แค่ IDiceRoller ตัวเดียว) เพราะนี่คือข้อบังคับสถาปัตยกรรม
/// "seed เดียวกัน + expression เดียวกัน ต้อง replay ได้ตรงเป๊ะ" — PROJECT-CONTEXT §3/§4
/// </summary>
public class DiceEnginePropertyAndReplayTests
{
    [Fact]
    public void RollSystem_SameSeedSameExpression_ReplaysIdentically()
    {
        var engine = new DiceEngine();

        var resultA = engine.RollSystem("2d6+1d4+3", new SeededDiceRoller(2024), seed: 2024);
        var resultB = engine.RollSystem("2d6+1d4+3", new SeededDiceRoller(2024), seed: 2024);

        Assert.Equal(resultA.Total, resultB.Total);
        Assert.Equal(
            resultA.Rolls.Select(r => (r.Sides, r.Value, r.Dropped, r.Sign)),
            resultB.Rolls.Select(r => (r.Sides, r.Value, r.Dropped, r.Sign)));
    }

    [Fact]
    public void RollSystem_DifferentSeedSameExpression_LikelyDifferentTotal()
    {
        var engine = new DiceEngine();

        // ทอยหลายลูก (10d20) เพื่อลดโอกาส false negative จากความบังเอิญ
        var resultA = engine.RollSystem("10d20", new SeededDiceRoller(1), seed: 1);
        var resultB = engine.RollSystem("10d20", new SeededDiceRoller(2), seed: 2);

        var valuesA = resultA.Rolls.Select(r => r.Value).ToArray();
        var valuesB = resultB.Rolls.Select(r => r.Value).ToArray();

        Assert.NotEqual(valuesA, valuesB);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RollSystem_TotalAlwaysWithinTheoreticalBounds(int seedBase)
    {
        var engine = new DiceEngine();
        const string expression = "2d6+1d4+3";
        const int min = 2 * 1 + 1 * 1 + 3; // 6
        const int max = 2 * 6 + 1 * 4 + 3; // 19

        for (var i = 0; i < 200; i++)
        {
            var seed = seedBase * 10_000 + i;
            var result = engine.RollSystem(expression, new SeededDiceRoller(seed), seed: seed);

            Assert.InRange(result.Total, min, max);
            foreach (var roll in result.Rolls)
            {
                Assert.InRange(roll.Value, 1, roll.Sides);
            }
        }
    }

    [Fact]
    public void RollSystem_AdvantageTotal_AlwaysWithinD20Bounds()
    {
        var engine = new DiceEngine();

        for (var seed = 0; seed < 200; seed++)
        {
            var result = engine.RollSystem(
                "1d20", new SeededDiceRoller(seed), seed: seed, advantage: AdvantageMode.Advantage);

            Assert.InRange(result.Total, 1, 20);
            Assert.Equal(2, result.Rolls.Count);
        }
    }
}

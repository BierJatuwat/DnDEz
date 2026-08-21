namespace DnDEz.Rules.Tests;

/// <summary>
/// ข้อบังคับสถาปัตยกรรม: RNG อยู่ฝั่ง server + seed แล้ว replay ได้ตรงเป๊ะ (PROJECT-CONTEXT §3/§4)
/// เทสต์ชุดนี้คือหลักฐานยืนยันข้อบังคับนี้โดยตรง
/// </summary>
public class SeededDiceRollerTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var rollerA = new SeededDiceRoller(seed: 42);
        var rollerB = new SeededDiceRoller(seed: 42);

        var sequenceA = Enumerable.Range(0, 20).Select(_ => rollerA.Roll(20)).ToArray();
        var sequenceB = Enumerable.Range(0, 20).Select(_ => rollerB.Roll(20)).ToArray();

        Assert.Equal(sequenceA, sequenceB);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var rollerA = new SeededDiceRoller(seed: 1);
        var rollerB = new SeededDiceRoller(seed: 2);

        var sequenceA = Enumerable.Range(0, 20).Select(_ => rollerA.Roll(20)).ToArray();
        var sequenceB = Enumerable.Range(0, 20).Select(_ => rollerB.Roll(20)).ToArray();

        // โอกาสที่ 20 ค่าจะซ้ำกันทุกตัวโดยบังเอิญ (1/20)^20 ต่ำจนถือว่าเป็นศูนย์ในทางปฏิบัติ
        // ถ้าเทสต์นี้ fail ให้สงสัยว่า seed ไม่ถูกใช้จริง (bug คอขาดบาดตายฝั่ง anti-cheat)
        Assert.NotEqual(sequenceA, sequenceB);
    }

    [Fact]
    public void Seed_ExposedAsProperty_MatchesConstructorArgument()
    {
        var roller = new SeededDiceRoller(seed: 123456789);
        Assert.Equal(123456789, roller.Seed);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(100)]
    public void Roll_AlwaysWithinRange(int sides)
    {
        var roller = new SeededDiceRoller(seed: 7);

        for (var i = 0; i < 1000; i++)
        {
            var value = roller.Roll(sides);
            Assert.InRange(value, 1, sides);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Roll_SidesBelowMinimum_Throws(int sides)
    {
        var roller = new SeededDiceRoller(seed: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => roller.Roll(sides));
    }
}

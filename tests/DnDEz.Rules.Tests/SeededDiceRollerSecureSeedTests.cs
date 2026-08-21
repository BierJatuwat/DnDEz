namespace DnDEz.Rules.Tests;

/// <summary>
/// เทสต์ยืนยัน SeededDiceRoller.CreateWithSecureRandomSeed() — factory ที่ generate seed จาก
/// CSPRNG (RandomNumberGenerator) แทนแหล่งที่เดาง่าย (TickCount/timestamp) เพราะ seed เป็น
/// anti-cheat boundary ที่ถูกเก็บลง DB จริง (PROJECT-CONTEXT §3 — ข้อบังคับสถาปัตยกรรม)
/// เขียนเทสต์นี้เพิ่มหลัง implementation มีอยู่แล้ว (ไม่ใช่ red-phase ของ implementation เดิม)
/// </summary>
public class SeededDiceRollerSecureSeedTests
{
    [Fact]
    public void CreateWithSecureRandomSeed_ProducesDifferentSeedsAcrossCalls()
    {
        var seeds = Enumerable.Range(0, 20)
            .Select(_ => SeededDiceRoller.CreateWithSecureRandomSeed().Seed)
            .ToArray();

        // ต้องไม่ใช่ค่าคงที่/ซ้ำกันหมด — ถ้า factory ผูกกับแหล่งที่เดาง่ายหรือค่าคงที่ เทสต์นี้จะจับได้
        Assert.True(seeds.Distinct().Count() > 1,
            "seed ที่ได้จาก CreateWithSecureRandomSeed() ซ้ำกันหมด — สงสัยว่าไม่ได้ใช้ CSPRNG จริง");
    }

    [Fact]
    public void CreateWithSecureRandomSeed_SeedIsNotDefaultZero()
    {
        // ไม่ควรมี pattern ที่ seed เป็น 0 บ่อยผิดปกติ (สัญญาณว่า buffer ไม่ได้ถูกเติมค่าจริง)
        var zeroCount = Enumerable.Range(0, 50)
            .Count(_ => SeededDiceRoller.CreateWithSecureRandomSeed().Seed == 0);

        Assert.True(zeroCount == 0,
            $"seed เป็น 0 ทั้งหมด {zeroCount}/50 ครั้ง — สงสัยว่า RandomNumberGenerator ไม่ได้ทำงานจริง");
    }

    [Fact]
    public void CreateWithSecureRandomSeed_RollerProducesValuesWithinRange()
    {
        var roller = SeededDiceRoller.CreateWithSecureRandomSeed();

        for (var i = 0; i < 100; i++)
        {
            Assert.InRange(roller.Roll(20), 1, 20);
        }
    }

    [Fact]
    public void CreateWithSecureRandomSeed_TwoInstances_ReplayIndependentlyBySeed()
    {
        var roller = SeededDiceRoller.CreateWithSecureRandomSeed();
        var replay = new SeededDiceRoller(roller.Seed);

        var original = Enumerable.Range(0, 10).Select(_ => roller.Roll(20)).ToArray();
        var replayed = Enumerable.Range(0, 10).Select(_ => replay.Roll(20)).ToArray();

        // seed เดียวกัน (เอามาจาก .Seed ของตัวที่สุ่มแล้ว) ต้อง replay ได้ตรงเป๊ะ — พิสูจน์ว่า
        // secure-seed path ยังคงอยู่ใน "deterministic-by-seed" contract เดียวกับ constructor ปกติ
        Assert.Equal(original, replayed);
    }
}

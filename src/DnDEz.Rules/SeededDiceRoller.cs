using System.Security.Cryptography;

namespace DnDEz.Rules;

/// <summary>
/// IDiceRoller ตัวจริงสำหรับ production — seed แล้ว replay ได้ตรงเป๊ะ: สร้างสองอินสแตนซ์ด้วย
/// seed เดียวกัน แล้วเรียก Roll() ลำดับเดียวกัน ต้องได้ผลลัพธ์เหมือนกันทุกบิต
///
/// Algorithm: SplitMix64 (Vigna) — deterministic PRNG ที่ใช้ full 64-bit seed space ทั้งก้อน
/// (ต่างจากการยัด long seed ลง System.Random(int) ที่ตัดทอนเหลือ 32-bit แล้วเสี่ยง seed ชนกัน)
/// เลือกเพราะ: (1) implementation สั้น ไม่มี state ซ่อนนอกเหนือจาก seed ทำให้ replay ตรวจสอบง่าย
/// (2) ผ่าน statistical test มาตรฐาน (เพียงพอสำหรับเกมลูกเต๋า ไม่ใช่ cryptographic use)
/// (3) ไม่ผูกกับ behavior ของ System.Random ที่เป็น implementation detail ของ runtime ซึ่งเคย
/// เปลี่ยน algorithm ข้าม major version มาแล้ว — เขียนเองให้ deterministic ข้าม .NET version แน่นอน
/// </summary>
public sealed class SeededDiceRoller : IDiceRoller
{
    public long Seed { get; }

    private ulong _state;

    public SeededDiceRoller(long seed)
    {
        Seed = seed;
        _state = unchecked((ulong)seed);
    }

    public int Roll(int sides)
    {
        if (sides < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sides), sides, "ลูกเต๋าต้องมีอย่างน้อย 2 หน้า");
        }

        var raw = NextUInt64();
        // Bias จาก modulo มีอยู่จริงแต่เล็กน้อยมากที่ sides <= 1000 เทียบกับ 2^64 (ไม่ใช่ cryptographic
        // use — เป็นเกมลูกเต๋า ความเที่ยงตรงระดับนี้เกินพอ ไม่ต้องทำ rejection sampling ให้ซับซ้อนขึ้น)
        return (int)(raw % (ulong)sides) + 1;
    }

    private ulong NextUInt64()
    {
        // SplitMix64 — ref: Vigna, "splitmix64" (public domain reference implementation)
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// สร้าง SeededDiceRoller ด้วย seed ที่มาจาก CSPRNG จริง (RandomNumberGenerator) แทนที่จะให้
    /// caller คิด seed เอง (กัน seed คาดเดาได้/ชนกันข้าม request ซึ่งเป็นช่องโกงถ้าใช้ตัวคาดเดาง่าย
    /// เช่น timestamp) — seed ที่ได้ยังคง expose ผ่าน Seed property ตามปกติ ระบบเก็บไว้ replay
    /// ย้อนหลังได้เหมือน seed ที่กำหนดเอง ต่างกันแค่ "ที่มา" ของ seed ตอนสร้างครั้งแรก
    /// </summary>
    public static SeededDiceRoller CreateWithSecureRandomSeed()
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        RandomNumberGenerator.Fill(buffer);
        var seed = BitConverter.ToInt64(buffer);
        return new SeededDiceRoller(seed);
    }
}

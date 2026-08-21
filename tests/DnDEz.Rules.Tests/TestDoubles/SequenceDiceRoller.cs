using DnDEz.Rules;

namespace DnDEz.Rules.Tests.TestDoubles;

/// <summary>
/// Fake IDiceRoller ที่คืนค่าตามคิวที่ตั้งไว้ล่วงหน้า — ใช้ทดสอบ logic ของ DiceEngine
/// (kh/kl, sign, advantage) แบบไม่พึ่งพา algorithm ของ RNG จริง
/// </summary>
public sealed class SequenceDiceRoller : IDiceRoller
{
    private readonly Queue<int> _values;

    /// <summary>บันทึกว่า engine เรียก Roll(sides) ด้วยค่า sides อะไรบ้าง เรียงตามลำดับ — ใช้ตรวจว่า
    /// engine ขอทอยลูกเต๋าถูกจำนวน/ถูกขนาดจริง</summary>
    public List<int> RequestedSides { get; } = new();

    public SequenceDiceRoller(params int[] values)
    {
        _values = new Queue<int>(values);
    }

    public int Roll(int sides)
    {
        RequestedSides.Add(sides);
        if (_values.Count == 0)
        {
            throw new InvalidOperationException(
                "SequenceDiceRoller: engine เรียก Roll() มากกว่าจำนวนค่าที่เทสต์เตรียมไว้ให้ — " +
                "แปลว่า engine ทอยลูกเต๋าเกินที่ควร หรือ validate guard rail ช้าเกินไป (ทอยไปแล้วก่อนเช็ค)");
        }
        return _values.Dequeue();
    }
}

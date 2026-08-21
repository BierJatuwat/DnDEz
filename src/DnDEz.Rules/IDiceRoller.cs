namespace DnDEz.Rules;

/// <summary>
/// Abstraction ของ RNG ที่ engine ใช้ทอยลูกเต๋าแต่ละลูก — ฉีดเข้า DiceEngine เสมอ ทำให้ engine
/// deterministic ทั้งก้อน (เทสต์ inject fake ที่คุมค่าได้, production inject SeededDiceRoller)
/// PROJECT-CONTEXT §3/§4 — "RNG อยู่ฝั่ง server + เก็บ seed เพื่อ replay ตรวจข้อพิพาทย้อนหลัง"
/// </summary>
public interface IDiceRoller
{
    /// <summary>ทอยลูกเต๋าหนึ่งลูก คืนค่าในช่วง 1..sides (inclusive ทั้งสองด้าน)</summary>
    int Roll(int sides);
}

namespace DnDEz.Api.Encounters;

/// <summary>
/// รายการ initiative ของตัวละครหนึ่งตัวในเอนเคาน์เตอร์ — ค่าที่ทอยได้ (1-20) แยกจาก modifier
/// ที่มาจาก DEX ตอนคำนวณเพื่อให้เก็บ log การทอยดิบไว้ตรวจสอบย้อนหลังได้
/// </summary>
public readonly record struct InitiativeEntry(Guid CharacterId, int InitiativeRoll, int InitiativeModifier)
{
    public int InitiativeTotal => InitiativeRoll + InitiativeModifier;
}

/// <summary>
/// เรียงลำดับ initiative ของเอนเคาน์เตอร์ด้วย tiebreak 3 ชั้น:
///   1. <see cref="InitiativeEntry.InitiativeTotal"/> มาก่อน (roll + modifier)
///   2. ถ้า total เท่ากัน — <see cref="InitiativeEntry.InitiativeModifier"/> สูงกว่ามาก่อน
///      (ตัวละคร DEX สูงตัดสินใจ/ขยับก่อนตามสัญชาตญาณกฎ 5e ที่ modifier สูงกว่าคือ "ไวกว่า")
///   3. ถ้ายังเท่ากันอีก — <see cref="InitiativeEntry.CharacterId"/> น้อยกว่ามาก่อน (tiebreak แบบ
///      deterministic ล้วนๆ ไม่มีความหมายทางกฎ)
///
/// ทำไมต้องมีชั้นที่ 3: `OrderByDescending`/`ThenByDescending` ของ LINQ (และ `List.Sort` ที่ใช้
/// introsort ภายใน) ไม่ใช่ stable sort เมื่อไม่มี key เทียบให้ครบทุกคู่ที่เท่ากัน ลำดับของรายการที่
/// key เท่ากันสนิททุกชั้นจึงไม่รับประกันว่าจะเหมือนเดิมทุกครั้งที่เรียก — ถ้าไม่ปิดด้วย CharacterId
/// ลำดับอาจสลับกันได้จริงระหว่างการอ่านสองครั้งของเอนเคาน์เตอร์เดียวกัน (เช่น re-render UI หรือ
/// GET ซ้ำ) ซึ่งทำให้ผู้เล่นเห็นลำดับเทิร์นขยับเองโดยไม่มีเหตุการณ์ทางกฎเกิดขึ้นจริง
///
/// ⚠️ ชั้นที่ 3 เป็นการตัดสินใจ MVP ของระบบนี้เท่านั้น ไม่ใช่กฎ 5e จริง — กฎ 5e ตัวจริงเมื่อ total
/// และ modifier เท่ากันสนิท ให้ DM ตัดสินใจเอง หรือให้ผู้เล่นที่เกี่ยวข้องทอยแก้ (roll-off) ระบบนี้
/// ยังไม่มี flow ให้ DM ตัดสินใจหรือให้ทอยแก้ตรงจุดนี้ — เมื่อมี flow นั้นในอนาคต ชั้นที่ 3 นี้ควร
/// ถูกแทนที่ ไม่ใช่ยึดเป็นกฎถาวร
/// </summary>
public static class InitiativeOrder
{
    public static IReadOnlyList<InitiativeEntry> Resolve(IEnumerable<InitiativeEntry> entries) =>
        entries
            .OrderByDescending(e => e.InitiativeTotal)
            .ThenByDescending(e => e.InitiativeModifier)
            .ThenBy(e => e.CharacterId)
            .ToList();
}

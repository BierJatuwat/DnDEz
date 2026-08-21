namespace DnDEz.Rules;

/// <summary>ที่มาของผลทอย — engine ประกาศ type ของตัวเอง ไม่พึ่ง DnDEz.Domain.Enums.DiceMode
/// เพื่อกัน coupling กับ Domain (Rules ห้าม reference Infrastructure/orchestrator และ reference
/// Domain เฉพาะเมื่อจำเป็นจริง ๆ เท่านั้น — ที่นี่ไม่จำเป็น ประกาศเองสะอาดกว่า) — PROJECT-CONTEXT §4</summary>
public enum DiceSource
{
    SystemRoll,
    DmEntered
}

/// <summary>advantage/disadvantage ใช้ได้เฉพาะกับ term d20 เดี่ยวตัวแรกของ expression (กติกา D&D 2024)</summary>
public enum AdvantageMode
{
    None,
    Advantage,
    Disadvantage
}

public enum KeepKind
{
    Highest,
    Lowest
}

/// <summary>เช่น "kh3" หรือ "kl1" ต่อท้ายกลุ่มลูกเต๋า</summary>
public sealed record KeepSpec(KeepKind Kind, int Amount);

/// <summary>
/// หนึ่งกลุ่มลูกเต๋าใน expression เช่น "4d6" หรือ "2d4" ใน "1d20+2d4+3"
/// Sign คือเครื่องหมายที่นำหน้ากลุ่มนี้ (+1 หรือ -1) สำหรับกรณี "1d20-2d4"
/// </summary>
public sealed record DiceGroupSpec(int Count, int Sides, int Sign, KeepSpec? Keep);

/// <summary>ผลจากการ parse expression ดิบ — เป็นแค่โครงสร้าง ยังไม่มีผลทอยจริง</summary>
public sealed record ParsedDiceExpression(IReadOnlyList<DiceGroupSpec> Groups, int FlatModifier);

/// <summary>โยนเมื่อ expression parse ไม่ได้ หรือชนกฎ guard rail (ขนาดเกิน, ค่าพิสดาร ฯลฯ)</summary>
public sealed class DiceExpressionException : Exception
{
    public DiceExpressionException(string message) : base(message) { }
}

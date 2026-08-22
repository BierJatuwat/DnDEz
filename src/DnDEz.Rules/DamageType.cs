namespace DnDEz.Rules;

/// <summary>13 damage type มาตรฐานของ SRD 5.2 — engine ประกาศเอง ไม่ผูกกับ Domain (เหตุผลเดียวกับ
/// DiceSource ใน DiceNotation.cs — Rules ห้าม reference Infrastructure/orchestrator)</summary>
public enum DamageType
{
    Acid,
    Bludgeoning,
    Cold,
    Fire,
    Force,
    Lightning,
    Necrotic,
    Piercing,
    Poison,
    Psychic,
    Radiant,
    Slashing,
    Thunder
}

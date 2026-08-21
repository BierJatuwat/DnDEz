namespace DnDEz.Rules;

/// <summary>
/// รวม advantage/disadvantage จากหลายแหล่ง (feature, condition, spell ฯลฯ) ให้เหลือ mode เดียว
/// ตามกติกา D&D 2024: มีทั้งสองพร้อมกัน = หักล้างกัน ทอยปกติ (ไม่ใช่ nested advantage-of-advantage)
/// </summary>
public static class AdvantageResolver
{
    public static AdvantageMode Resolve(bool hasAdvantage, bool hasDisadvantage)
    {
        if (hasAdvantage && hasDisadvantage)
        {
            return AdvantageMode.None;
        }

        if (hasAdvantage)
        {
            return AdvantageMode.Advantage;
        }

        if (hasDisadvantage)
        {
            return AdvantageMode.Disadvantage;
        }

        return AdvantageMode.None;
    }
}

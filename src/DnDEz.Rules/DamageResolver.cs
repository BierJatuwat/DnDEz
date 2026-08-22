namespace DnDEz.Rules;

/// <summary>หนึ่ง component ของ damage ในการโจมตีเดียว เช่น "1d8" slashing + "2d6" fire จากอาวุธเวทมนตร์</summary>
public sealed record DamageComponent(string Expression, DamageType Type);

/// <summary>ผลของ component เดียวหลังทอย + หัก resistance/vulnerability/immunity แล้ว</summary>
public sealed record DamageComponentResult(DamageType Type, RollResult Roll, int RawAmount, int AdjustedAmount);

/// <summary>ผลรวมของ damage หลายชนิดในการโจมตีเดียว — TotalAdjustedAmount คือยอดที่เอาไปหัก HP จริง</summary>
public sealed record DamageRollResult(IReadOnlyList<DamageComponentResult> Components, int TotalAdjustedAmount);

/// <summary>
/// จุดเข้าใช้งานหลักสำหรับทอย damage หลาย component พร้อมกัน แล้วหัก resistance/vulnerability/immunity
/// ต่อ component (คนละ damage type อาจโดนหักคนละแบบในการโจมตีเดียว) — ใช้ DiceEngine.RollDamage
/// ต่อ component แล้วส่งต่อให้ DamageTypeResolver.Resolve ตัดสิน ไม่มี dice-rolling logic ของตัวเอง
/// </summary>
public static class DamageResolver
{
    public static DamageRollResult RollAndResolve(
        DiceEngine engine,
        IReadOnlyList<DamageComponent> components,
        IDiceRoller roller,
        bool isCritical,
        IReadOnlySet<DamageType> resistances,
        IReadOnlySet<DamageType> vulnerabilities,
        IReadOnlySet<DamageType> immunities,
        long? seed = null)
    {
        var results = new List<DamageComponentResult>(components.Count);
        var total = 0;

        foreach (var component in components)
        {
            var roll = engine.RollDamage(component.Expression, roller, isCritical, seed);
            var rawAmount = roll.Total;
            var clampedAmount = Math.Max(0, rawAmount);
            var adjustedAmount = DamageTypeResolver.Resolve(
                component.Type, clampedAmount, resistances, vulnerabilities, immunities);

            results.Add(new DamageComponentResult(component.Type, roll, rawAmount, adjustedAmount));
            total += adjustedAmount;
        }

        return new DamageRollResult(results, total);
    }
}

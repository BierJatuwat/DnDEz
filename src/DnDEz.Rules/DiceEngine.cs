namespace DnDEz.Rules;

/// <summary>รายละเอียดลูกเต๋าลูกเดียวในผลทอย — เก็บไว้เพื่อความโปร่งใส/audit แม้ลูกที่โดน drop (kh/kl)</summary>
public sealed record DieRollDetail(int Sides, int Value, bool Dropped, int Sign);

/// <summary>
/// ผลลัพธ์การทอยที่ engine คืนกลับ — เป็น record ล้วน ไม่ใช่ entity (engine ไม่เขียน DB เอง
/// การ persist เป็นหน้าที่ชั้น application) — PROJECT-CONTEXT §4
/// </summary>
public sealed record RollResult(
    string Expression,
    IReadOnlyList<DieRollDetail> Rolls,
    int Total,
    DiceSource Source,
    long? Seed,
    AdvantageMode Advantage);

/// <summary>
/// จุดเข้าใช้งานหลักของ Phase 1: ทอยผ่าน RNG (SystemRoll) หรือรับเลขดิบจาก DM (DmEntered)
/// engine คำนวณผลรวม/kh-kl/advantage เหมือนกันทั้งสองทาง ต่างแค่ที่มาของตัวเลขดิบ
/// player-side-design §5 — "ผู้เล่นกรอกผลเองไม่ได้" คือ authorization ชั้นบน engine ไม่รู้จัก user/role เลย
/// (ตาม PROJECT-CONTEXT §4: "engine ไม่รู้จัก concept ผู้ใช้/สิทธิ์เลย") หน้าที่ engine แค่รับ
/// รายการเลขดิบมาคำนวณให้ตรง — การเช็คว่าใครมีสิทธิ์กรอกเป็นหน้าที่ชั้น application/authorization
/// </summary>
public sealed class DiceEngine
{
    /// <summary>ทอยด้วย RNG ที่ฉีดเข้ามา (deterministic ถ้า roller เป็น SeededDiceRoller เดิม)</summary>
    public RollResult RollSystem(
        string expression,
        IDiceRoller roller,
        long? seed = null,
        AdvantageMode advantage = AdvantageMode.None)
    {
        var parsed = DiceExpressionParser.Parse(expression);

        if (advantage != AdvantageMode.None)
        {
            ValidateAdvantageShape(parsed);

            // validate ก่อนทอยเสมอ — ห้ามเรียก roller.Roll() ถ้า shape ไม่ตรง (กัน "ทอยไปแล้วก่อนเช็ค")
            var first = roller.Roll(20);
            var second = roller.Roll(20);

            return BuildAdvantageResult(
                expression, DiceSource.SystemRoll, seed, advantage, first, second, parsed.FlatModifier);
        }

        return BuildNormalResult(
            expression, DiceSource.SystemRoll, seed, advantage, parsed,
            group => RollGroupFromRoller(group, roller));
    }

    /// <summary>
    /// รับเลขดิบจาก DM (เต๋าจริงบนโต๊ะ) — rawValues ต้องมีจำนวนตรงกับที่ expression (+advantage ถ้ามี)
    /// ต้องการเป๊ะ ๆ และแต่ละค่าต้องอยู่ในช่วง [1, sides] ของกลุ่มที่มันสังกัด ไม่งั้นโยน DiceExpressionException
    /// </summary>
    public RollResult RollFromDmEntered(
        string expression,
        IReadOnlyList<int> rawValues,
        AdvantageMode advantage = AdvantageMode.None)
    {
        if (rawValues is null)
        {
            throw new DiceExpressionException("ต้องระบุ rawValues สำหรับ DmEntered");
        }

        var parsed = DiceExpressionParser.Parse(expression);

        if (advantage != AdvantageMode.None)
        {
            ValidateAdvantageShape(parsed);

            if (rawValues.Count != 2)
            {
                throw new DiceExpressionException(
                    $"Advantage/Disadvantage ต้องการเลขดิบพอดี 2 ค่า (ได้ {rawValues.Count})");
            }

            var first = rawValues[0];
            var second = rawValues[1];
            ValidateRawRange(first, 20);
            ValidateRawRange(second, 20);

            return BuildAdvantageResult(
                expression, DiceSource.DmEntered, seed: null, advantage, first, second, parsed.FlatModifier);
        }

        var expectedCount = parsed.Groups.Sum(g => g.Count);
        if (rawValues.Count != expectedCount)
        {
            throw new DiceExpressionException(
                $"จำนวนเลขดิบที่กรอกไม่ตรงกับที่ expression ต้องการ " +
                $"(ต้องการ {expectedCount} ได้รับ {rawValues.Count})");
        }

        var cursor = 0;
        return BuildNormalResult(
            expression, DiceSource.DmEntered, seed: null, advantage, parsed,
            group => RollGroupFromRawValues(group, rawValues, ref cursor));
    }

    /// <summary>
    /// ทอย damage — ตอน isCritical=true ให้ทอยลูกเต๋าความเสียหายเป็น "สองเท่าของจำนวนลูก" ต่อกลุ่ม
    /// (ดับเบิล Count ของแต่ละ DiceGroupSpec) ไม่ใช่ทอยครั้งเดียวแล้วคูณผลรวม×2 และไม่แตะ FlatModifier
    /// เลย (D&D 2024: crit ดับเบิลเฉพาะเต๋า ไม่ดับเบิล modifier) — reuse BuildNormalResult/
    /// RollGroupFromRoller เดิมของ Phase 1 ทั้งหมด ไม่มี dice-rolling logic ใหม่ในเมธอดนี้
    ///
    /// ตัดสินใจเรื่อง kh/kl บน crit (damage ปกติไม่ใช้ kh/kl แต่ parser Phase 1 ไม่ได้ห้าม):
    /// Keep.Amount **ไม่ถูกดับเบิลตาม** เมื่อดับเบิล Count — เหตุผล: "kh3" ที่ผู้ใช้ตั้งค่าไว้แปลว่า
    /// "เก็บสูงสุด 3 เม็ดที่ดีที่สุด" ซึ่งเป็นความตั้งใจของผู้ใช้ตรง ๆ ไม่มีกฎ D&D ข้อไหนบอกว่าจำนวนที่
    /// "เก็บ" ต้องโตตามจำนวนที่ "ทอย" เพิ่มจาก crit ถ้าดับเบิล Keep.Amount ไปด้วยจะเปลี่ยนความหมายของ
    /// การตั้งค่าเดิมโดยไม่มีที่มา ส่วนการดับเบิลแค่ Count (pool ที่ทอย) แล้วคง Keep.Amount เดิมไว้
    /// ยังคงเป็นพฤติกรรมที่คาดเดาได้ตรงไปตรงมาที่สุด — ดูเทสต์ยืนยันใน DiceEngineDamageTests
    /// </summary>
    public RollResult RollDamage(
        string expression,
        IDiceRoller roller,
        bool isCritical,
        long? seed = null)
    {
        var parsed = DiceExpressionParser.Parse(expression);

        if (isCritical)
        {
            var doubledGroups = parsed.Groups
                .Select(group => group with { Count = group.Count * 2 })
                .ToList();
            parsed = new ParsedDiceExpression(doubledGroups, parsed.FlatModifier);
        }

        return BuildNormalResult(
            expression, DiceSource.SystemRoll, seed, AdvantageMode.None, parsed,
            group => RollGroupFromRoller(group, roller));
    }

    private static RollResult BuildAdvantageResult(
        string expression,
        DiceSource source,
        long? seed,
        AdvantageMode advantage,
        int first,
        int second,
        int flatModifier)
    {
        // เท่ากัน: ไม่มีใครถูก drop จริง ๆ — เก็บตัวแรกไว้เป็น kept เพื่อความสม่ำเสมอ ผลรวมเหมือนกันอยู่แล้ว
        var firstDropped = advantage == AdvantageMode.Advantage
            ? first < second
            : first > second;
        if (first == second)
        {
            firstDropped = false;
        }

        var secondDropped = !firstDropped;

        var rolls = new List<DieRollDetail>
        {
            new(20, first, firstDropped, 1),
            new(20, second, secondDropped, 1)
        };

        var keptValue = firstDropped ? second : first;
        var total = keptValue + flatModifier;

        return new RollResult(expression, rolls, total, source, seed, advantage);
    }

    private static RollResult BuildNormalResult(
        string expression,
        DiceSource source,
        long? seed,
        AdvantageMode advantage,
        ParsedDiceExpression parsed,
        Func<DiceGroupSpec, int[]> rollGroup)
    {
        var allRolls = new List<DieRollDetail>();
        var runningTotal = parsed.FlatModifier;

        foreach (var group in parsed.Groups)
        {
            var values = rollGroup(group);
            var dropped = ResolveDropped(values, group.Keep);

            for (var i = 0; i < values.Length; i++)
            {
                allRolls.Add(new DieRollDetail(group.Sides, values[i], dropped[i], group.Sign));
                if (!dropped[i])
                {
                    runningTotal += group.Sign * values[i];
                }
            }
        }

        return new RollResult(expression, allRolls, runningTotal, source, seed, advantage);
    }

    private static int[] RollGroupFromRoller(DiceGroupSpec group, IDiceRoller roller)
    {
        var values = new int[group.Count];
        for (var i = 0; i < group.Count; i++)
        {
            values[i] = roller.Roll(group.Sides);
        }

        return values;
    }

    private static int[] RollGroupFromRawValues(DiceGroupSpec group, IReadOnlyList<int> rawValues, ref int cursor)
    {
        var values = new int[group.Count];
        for (var i = 0; i < group.Count; i++)
        {
            var value = rawValues[cursor++];
            ValidateRawRange(value, group.Sides);
            values[i] = value;
        }

        return values;
    }

    /// <summary>advantage/disadvantage ใช้ได้เฉพาะ expression ที่เป็น d20 เดี่ยวตัวแรกล้วน ๆ
    /// (ไม่มี kh/kl, ไม่มีเครื่องหมายลบนำหน้า, ไม่มีกลุ่มอื่นปน — modifier ธรรมดา (+N) ยังใช้ได้)</summary>
    private static void ValidateAdvantageShape(ParsedDiceExpression parsed)
    {
        if (parsed.Groups.Count != 1)
        {
            throw new DiceExpressionException(
                "Advantage/Disadvantage ใช้ได้เฉพาะ expression ที่มีกลุ่มลูกเต๋าเดียวคือ d20 เท่านั้น");
        }

        var group = parsed.Groups[0];
        if (group.Count != 1 || group.Sides != 20 || group.Sign != 1 || group.Keep is not null)
        {
            throw new DiceExpressionException(
                "Advantage/Disadvantage ใช้ได้เฉพาะกับ term d20 เดี่ยวตัวแรกที่ไม่มี kh/kl " +
                "หรือเครื่องหมายลบนำหน้าเท่านั้น");
        }
    }

    private static void ValidateRawRange(int value, int sides)
    {
        if (value < 1 || value > sides)
        {
            throw new DiceExpressionException(
                $"ค่าที่กรอก {value} ต้องอยู่ในช่วง 1..{sides}");
        }
    }

    /// <summary>คำนวณว่าลูกไหนโดน drop ตาม kh/kl — เก็บลำดับเดิมของผลทอยไว้ (ไม่ sort list จริง)
    /// เพื่อให้ audit trail ตรงกับลำดับที่ทอย/กรอกจริง</summary>
    private static bool[] ResolveDropped(int[] values, KeepSpec? keep)
    {
        var dropped = new bool[values.Length];
        if (keep is null)
        {
            return dropped;
        }

        var indices = Enumerable.Range(0, values.Length).ToList();
        var dropCount = values.Length - keep.Amount;

        var orderedForDrop = keep.Kind == KeepKind.Highest
            ? indices.OrderBy(i => values[i]).ThenBy(i => i)         // เก็บสูงสุด -> drop จากค่าน้อยสุดก่อน
            : indices.OrderByDescending(i => values[i]).ThenBy(i => i); // เก็บต่ำสุด -> drop จากค่ามากสุดก่อน

        foreach (var idx in orderedForDrop.Take(dropCount))
        {
            dropped[idx] = true;
        }

        return dropped;
    }
}

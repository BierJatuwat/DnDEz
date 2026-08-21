using System.Text.RegularExpressions;

namespace DnDEz.Rules;

/// <summary>
/// Parser ของ dice notation "XdY+Z" รองรับ kh/kl และหลาย term เช่น "1d20+2d4-3"
/// รับ input จากภายนอกได้ (DM/player พิมพ์) → ต้องมี guard rail กัน DoS จาก input พิสดาร
/// (เช่น "999999d20") — S1 spec ใน PROJECT-CONTEXT §4
/// </summary>
public static class DiceExpressionParser
{
    // Guard rail — ตัวเลขเหล่านี้เป็น "สัญญา" ที่เทสต์อ้างอิง ห้ามแก้โดยไม่คุยกับ owner
    public const int MaxDiceCountPerGroup = 100;
    public const int MaxGroupsPerExpression = 20;
    public const int MaxSides = 1000;
    public const int MinSides = 2;
    public const int MaxExpressionLength = 200;

    // หนึ่ง term คือกลุ่มลูกเต๋า "XdY" (มี kh/kl ต่อท้ายได้) หรือตัวเลขล้วน (flat modifier)
    // ใช้ [0-9] แทน \d โดยตั้งใจ: .NET regex `\d` เป็น Unicode-aware และจับเลขนอกอารบิกด้วย
    // (เลขไทย ๑๒, อารบิก-อินดิก ١٢, fullwidth １２ ฯลฯ) ซึ่ง long.TryParse ไม่รองรับ ผลคือ
    // input พวกนี้จะหลุดผ่าน regex ไปตายที่ ParseGuarded ด้วยข้อความ "เกินขอบเขต" ซึ่งไม่ตรงความจริง
    // (ปัญหาจริงคือ syntax ไม่ถูกต้อง ไม่ใช่ค่าเกินขอบเขต) — เจาะจง [0-9] เพื่อให้ regex reject
    // ตั้งแต่ต้นทางด้วยข้อความ syntax ที่ถูกต้อง ห้ามเปลี่ยนกลับเป็น \d
    private const string TermPattern = @"[0-9]+D[0-9]+(?:K[HL][0-9]+)?|[0-9]+";

    private static readonly Regex FullExpressionRegex =
        new($@"^(?:{TermPattern})(?:[+-](?:{TermPattern}))*$", RegexOptions.Compiled);

    private static readonly Regex TokenRegex =
        new($@"[+-]?(?:{TermPattern})", RegexOptions.Compiled);

    // เหตุผลเดียวกับ TermPattern ด้านบน — [0-9] แทน \d กัน Unicode digit หลุดเข้ามาถึงจุดนี้
    // (แม้ในทางปฏิบัติ FullExpressionRegex กรองไปแล้ว แต่คงความเข้มงวดไว้ให้สอดคล้องกันทั้งไฟล์)
    private static readonly Regex DiceGroupRegex =
        new(@"^([0-9]+)D([0-9]+)(?:(KH|KL)([0-9]+))?$", RegexOptions.Compiled);

    /// <summary>
    /// Parse expression ดิบเป็นโครงสร้าง ไม่ทอยจริง
    /// โยน DiceExpressionException ถ้า: syntax ผิด, sides &lt; MinSides หรือ &gt; MaxSides,
    /// count &lt;= 0 หรือ &gt; MaxDiceCountPerGroup, จำนวนกลุ่ม &gt; MaxGroupsPerExpression,
    /// keep amount &lt;= 0 หรือมากกว่า count ของกลุ่มนั้น, expression ว่าง/null/ยาวเกิน MaxExpressionLength
    /// </summary>
    public static ParsedDiceExpression Parse(string expression)
    {
        if (expression is null)
        {
            throw new DiceExpressionException("Expression ห้ามเป็น null");
        }

        // เช็คความยาวดิบ "ก่อน" ตัด whitespace เสมอ — กัน DoS ที่ยัด whitespace/ตัวเลขมหาศาลมา
        // ถ้าตัด whitespace ก่อนแล้วค่อยเช็คความยาว จะหลุด guard นี้ไปได้ง่าย ๆ
        if (expression.Length > MaxExpressionLength)
        {
            throw new DiceExpressionException(
                $"Expression ยาวเกิน {MaxExpressionLength} ตัวอักษร");
        }

        // \s ที่นี่ยังคงเป็น Unicode-aware ตามเดิม (ไม่จำกัดเป็น [ \t\r\n]) โดยตั้งใจ — ต่างจาก
        // \d ด้านบนตรงที่การตัด Unicode whitespace ออกไม่เปิดช่องโหว่อะไร เพราะไม่ว่าจะตัดอะไรออกไปบ้าง
        // ผลลัพธ์ `compact` ก็ยังต้องผ่าน FullExpressionRegex ที่เข้มงวด (จำกัดด้วย [0-9] แล้ว) อยู่ดี
        // ก่อนจะถูกยอมรับ จึงไม่มี attack surface เพิ่มจากการปล่อย \s ไว้แบบเดิม
        var compact = Regex.Replace(expression, @"\s+", string.Empty).ToUpperInvariant();

        if (compact.Length == 0)
        {
            throw new DiceExpressionException("Expression ห้ามว่าง");
        }

        if (!FullExpressionRegex.IsMatch(compact))
        {
            throw new DiceExpressionException($"Expression syntax ไม่ถูกต้อง: '{expression}'");
        }

        var groups = new List<DiceGroupSpec>();
        long flatModifier = 0;

        foreach (Match token in TokenRegex.Matches(compact))
        {
            var body = token.Value;
            var sign = 1;
            if (body[0] is '+' or '-')
            {
                sign = body[0] == '-' ? -1 : 1;
                body = body[1..];
            }

            var diceMatch = DiceGroupRegex.Match(body);
            if (diceMatch.Success)
            {
                if (groups.Count >= MaxGroupsPerExpression)
                {
                    throw new DiceExpressionException(
                        $"จำนวนกลุ่มลูกเต๋าเกิน {MaxGroupsPerExpression} กลุ่ม");
                }

                var count = ParseGuarded(diceMatch.Groups[1].Value, "จำนวนลูกเต๋า");
                if (count <= 0 || count > MaxDiceCountPerGroup)
                {
                    throw new DiceExpressionException(
                        $"จำนวนลูกเต๋าต่อกลุ่มต้องอยู่ระหว่าง 1 ถึง {MaxDiceCountPerGroup} (ได้ {count})");
                }

                var sides = ParseGuarded(diceMatch.Groups[2].Value, "ขนาดลูกเต๋า");
                if (sides < MinSides || sides > MaxSides)
                {
                    throw new DiceExpressionException(
                        $"ขนาดลูกเต๋าต้องอยู่ระหว่าง {MinSides} ถึง {MaxSides} (ได้ {sides})");
                }

                KeepSpec? keep = null;
                if (diceMatch.Groups[3].Success)
                {
                    var amount = ParseGuarded(diceMatch.Groups[4].Value, "จำนวนลูกที่เก็บ (kh/kl)");
                    if (amount <= 0 || amount > count)
                    {
                        throw new DiceExpressionException(
                            "จำนวนลูกที่เก็บ (kh/kl) ต้องมากกว่า 0 และไม่เกินจำนวนลูกในกลุ่มนั้น");
                    }

                    var kind = diceMatch.Groups[3].Value == "KH" ? KeepKind.Highest : KeepKind.Lowest;
                    keep = new KeepSpec(kind, (int)amount);
                }

                groups.Add(new DiceGroupSpec((int)count, (int)sides, sign, keep));
            }
            else
            {
                var value = ParseGuarded(body, "ค่า modifier");
                flatModifier += sign * value;

                if (flatModifier is > int.MaxValue or < int.MinValue)
                {
                    throw new DiceExpressionException("ค่า modifier รวมเกินขอบเขตที่รองรับ");
                }
            }
        }

        if (groups.Count == 0)
        {
            throw new DiceExpressionException("Expression ต้องมีอย่างน้อยหนึ่งกลุ่มลูกเต๋า (XdY)");
        }

        return new ParsedDiceExpression(groups, (int)flatModifier);
    }

    /// <summary>parse ตัวเลขดิบเป็น long แบบไม่ปล่อย OverflowException/FormatException ดิบออกไป —
    /// ตัวเลขที่ยาวเกิน long ก็ถือว่าเป็น input พิสดารที่ reject ด้วย guard rail เหมือนกัน</summary>
    private static long ParseGuarded(string digits, string label)
    {
        if (!long.TryParse(digits, out var value))
        {
            throw new DiceExpressionException($"{label} มีค่าเกินขอบเขตที่ระบบรองรับ: '{digits}'");
        }

        return value;
    }
}

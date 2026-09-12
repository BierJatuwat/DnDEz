using System.Text;
using System.Text.Json;
using DnDEz.Rules;
using System.Linq;

namespace DnDEz.Api.Endpoints;

/// <summary>ชนิดการระบายช่องของ DM — ช่องหนึ่งช่องเป็นได้อย่างเดียว: ปกติ / พื้นเดินยาก / กำแพง</summary>
public enum TerrainPaint
{
    /// <summary>ทำเป็นพื้นเดินยาก (ถ้าเคยเป็นกำแพงจะเลิกเป็นกำแพง)</summary>
    Difficult,

    /// <summary>ทำเป็นกำแพง/สิ่งกีดขวางเต็มช่อง (ถ้าเคยเป็นพื้นเดินยากจะเลิกเป็นพื้นเดินยาก)</summary>
    Blocked,

    /// <summary>คืนเป็นพื้นปกติ — ลบออกจากทั้งสองเซ็ต</summary>
    Clear
}

/// <summary>
/// ผลของการระบาย · <paramref name="Succeeded"/> = false เมื่อจำนวนช่องรวมทั้งฉากหลังระบายจะเกิน
/// <see cref="SceneTerrainSerialization.MaxSquaresPerScene"/> — ตอนนั้น <paramref name="Json"/> เป็น null และ
/// <paramref name="Layer"/> คือพื้นเดิมที่ไม่ถูกแตะ
/// </summary>
public sealed record TerrainEditResult(bool Succeeded, string? Json, TerrainLayer Layer);

/// <summary>
/// Sprint H Part 5 — พื้นจากแผนที่ของฉาก (spatial-play-design §3.3 ชั้น "พื้นจากแผนที่")
/// เก็บในคอลัมน์ `scenes.terrain` (jsonb) รูปร่าง <c>{"difficult":[[x,y],...],"blocked":[[x,y],...]}</c>
/// พิกัดเป็น "ช่อง" หน่วยเดียวกับ token (Sprint G Part 10)
///
/// ## ทำไมเป็นคอลัมน์ใหม่ ไม่ขยาย `scenes.grid` ตามที่ §3.3 เขียนไว้
/// endpoint `PATCH .../grid` (SceneEndpoints) **สร้าง `grid` ใหม่ทั้งก้อน** ทุกครั้งที่ DM จัดกริด —
/// ถ้าพื้นอยู่ใน `grid` การปรับ pixelsPerSquare ครั้งเดียวจะลบกำแพงทั้งฉากทิ้งเงียบ ๆ · แยกคอลัมน์จึง
/// ปลอดภัยกว่าการไปแก้ grid PATCH ให้ merge (ซึ่งทุก endpoint ที่เขียน grid ในอนาคตต้องจำให้ได้)
/// · เป็นคอลัมน์บนตาราง `scenes` เดิม **ไม่ใช่ตารางใหม่** จึงอยู่ใต้ RLS policy `campaign_isolation`
/// ที่มีอยู่แล้ว (ENABLE + FORCE) ไม่ต้องสร้าง policy ใหม่
///
/// ## ⚠️ ชั้น effect (Web/Grease/Spike Growth) ยังไม่ทำ
/// §3.3 กำหนดให้พื้นจาก effect ผูกกับ encounter และลบทิ้งได้ทั้งชั้นตอนเวทหมดเวลา — ต้องมีที่เก็บผูก
/// encounter + วงจรชีวิตตามระยะเวลาเวท ซึ่งเป็นระบบแยกทั้งก้อน Sprint H Part 5 จึงทำแค่ชั้นแผนที่ ·
/// <see cref="ToTerrainField"/> ใส่ <see cref="TerrainLayer.Empty"/> ให้ชั้น effect **ที่เดียวทั้งระบบ**
/// ถ้าวันหนึ่งทำชั้น effect ให้แก้ที่นี่ที่เดียว
///
/// ## กันข้อมูลใหญ่ผิดปกติ (DoS)
/// Dijkstra ของ <see cref="MovementCostResolver.Reachable"/> กระจายแค่ในงบเดิน (speed 30 = 6 ช่อง)
/// ไม่ขึ้นกับขนาดแผนที่ และถามพื้นแบบ O(1) ผ่าน HashSet — ต้นทุนที่ขึ้นกับขนาดพื้นจริง ๆ คือการ parse
/// JSON ทุก request จึงจำกัดทั้งต่อครั้ง (<see cref="MaxSquaresPerEdit"/>) ต่อฉาก
/// (<see cref="MaxSquaresPerScene"/>) และขอบพิกัด (<see cref="MaxCoordinate"/>)
/// </summary>
public static class SceneTerrainSerialization
{
    /// <summary>จำนวนช่องสูงสุดต่อการระบายหนึ่งครั้ง — ระบายทีละแถบ ไม่ใช่ทั้งแผนที่ในคำขอเดียว</summary>
    public const int MaxSquaresPerEdit = 1_000;

    /// <summary>จำนวนช่องสูงสุดทั้งฉาก (พื้นเดินยาก + กำแพง รวมกัน) — แผนที่ 100×100 ช่องระบายได้ครบทุกช่อง</summary>
    public const int MaxSquaresPerScene = 10_000;

    /// <summary>ขอบพิกัด |x|, |y| ต้องไม่เกินค่านี้</summary>
    public const int MaxCoordinate = 1_000;

    /// <summary>
    /// อ่าน `scenes.terrain` แบบไม่ไว้ใจข้อมูลเลย (คอลัมน์ jsonb อิสระที่ไม่มี constraint บังคับรูปร่าง):
    /// null / สตริงว่าง / "{}" / JSON พัง / root ไม่ใช่ object / คีย์หายหรือไม่ใช่ array → ชั้นว่าง ไม่ throw ·
    /// แต่ละรายการต้องเป็น array ของจำนวนเต็ม 2 ตัวพอดี (<c>[x,y]</c>) ภายในขอบ <see cref="MaxCoordinate"/>
    /// รายการที่ผิดรูปข้ามทีละรายการ ไม่ทิ้งทั้งก้อน · ช่องที่อยู่ทั้งสองเซ็ตนับเป็นกำแพงอย่างเดียว ·
    /// อ่านสูงสุด <see cref="MaxSquaresPerScene"/> ช่องรวม ที่เกินจากนั้นไม่อ่าน
    /// </summary>
    public static TerrainLayer Parse(string? terrainJson)
    {
        if (string.IsNullOrWhiteSpace(terrainJson))
        {
            return TerrainLayer.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(terrainJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return TerrainLayer.Empty;
            }

            var blocked = new HashSet<GridPoint>();
            var difficult = new HashSet<GridPoint>();
            var total = 0;

            // อ่าน blocked ก่อน difficult เสมอ — ช่องที่อยู่ทั้งสองเซ็ตต้องนับเป็นกำแพงอย่างเดียว
            ParseSquares(doc.RootElement, "blocked", blocked, excludeIfPresent: null, ref total);
            ParseSquares(doc.RootElement, "difficult", difficult, excludeIfPresent: blocked, ref total);

            return TerrainLayer.FromSquares(difficult, blocked);
        }
        catch (JsonException)
        {
            return TerrainLayer.Empty;
        }
    }

    private static void ParseSquares(
        JsonElement root,
        string propertyName,
        HashSet<GridPoint> target,
        HashSet<GridPoint>? excludeIfPresent,
        ref int total)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (total >= MaxSquaresPerScene)
            {
                return;
            }

            if (!TryParseSquare(item, out var square))
            {
                continue;
            }

            if (excludeIfPresent is not null && excludeIfPresent.Contains(square))
            {
                continue;
            }

            if (target.Add(square))
            {
                total++;
            }
        }
    }

    private static bool TryParseSquare(JsonElement item, out GridPoint square)
    {
        square = default;

        if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2)
        {
            return false;
        }

        var enumerator = item.EnumerateArray();
        enumerator.MoveNext();
        var xElement = enumerator.Current;
        enumerator.MoveNext();
        var yElement = enumerator.Current;

        if (xElement.ValueKind != JsonValueKind.Number || yElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (!xElement.TryGetInt32(out var x) || !yElement.TryGetInt32(out var y))
        {
            return false;
        }

        var candidate = new GridPoint(x, y);
        if (!IsWithinBounds(candidate))
        {
            return false;
        }

        square = candidate;
        return true;
    }

    /// <summary>
    /// เขียนกลับเป็น JSON ด้วยคีย์ "difficult" กับ "blocked" ทุกครั้ง (แม้ว่าง) · แต่ละช่องเป็น <c>[x,y]</c>
    /// เรียงตาม Y แล้วตาม X — ผลลัพธ์เหมือนเดิมทุกครั้งสำหรับพื้นชุดเดียวกัน (diff ใน DB อ่านได้)
    /// </summary>
    public static string Serialize(TerrainLayer layer)
    {
        var sb = new StringBuilder();
        sb.Append("{\"difficult\":");
        AppendSquares(sb, layer.Difficult);
        sb.Append(",\"blocked\":");
        AppendSquares(sb, layer.Blocked);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendSquares(StringBuilder sb, IReadOnlySet<GridPoint> squares)
    {
        sb.Append('[');

        var first = true;
        foreach (var square in squares.OrderBy(s => s.Y).ThenBy(s => s.X))
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append('[')
                .Append(square.X.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(',')
                .Append(square.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(']');
        }

        sb.Append(']');
    }

    /// <summary>
    /// ระบายช่องชุดหนึ่งลงบนพื้นเดิม (พื้นเดิมที่ผิดรูปถือเป็นชั้นว่าง ไม่ throw):
    /// <see cref="TerrainPaint.Difficult"/> ใส่ในพื้นเดินยากและเอาออกจากกำแพง ·
    /// <see cref="TerrainPaint.Blocked"/> ใส่ในกำแพงและเอาออกจากพื้นเดินยาก ·
    /// <see cref="TerrainPaint.Clear"/> เอาออกจากทั้งคู่ · ระบายซ้ำช่องเดิมไม่เกิดรายการซ้ำ
    ///
    /// **ไม่ตรวจขอบพิกัดหรือจำนวนต่อครั้ง** (หน้าที่ของ endpoint) — ตรวจแค่เพดานทั้งฉาก
    /// </summary>
    public static TerrainEditResult Apply(string? currentJson, TerrainPaint paint, IReadOnlyCollection<GridPoint> squares)
    {
        var original = Parse(currentJson);

        var difficult = new HashSet<GridPoint>(original.Difficult);
        var blocked = new HashSet<GridPoint>(original.Blocked);

        foreach (var square in squares)
        {
            switch (paint)
            {
                case TerrainPaint.Difficult:
                    blocked.Remove(square);
                    difficult.Add(square);
                    break;
                case TerrainPaint.Blocked:
                    difficult.Remove(square);
                    blocked.Add(square);
                    break;
                case TerrainPaint.Clear:
                    difficult.Remove(square);
                    blocked.Remove(square);
                    break;
            }
        }

        if (difficult.Count + blocked.Count > MaxSquaresPerScene)
        {
            return new TerrainEditResult(false, null, original);
        }

        var newLayer = TerrainLayer.FromSquares(difficult, blocked);
        return new TerrainEditResult(true, Serialize(newLayer), newLayer);
    }

    /// <summary>|x| และ |y| ไม่เกิน <see cref="MaxCoordinate"/> — เทียบสองด้านตรง ๆ ไม่ใช้ <see cref="Math.Abs(int)"/>
    /// เพราะ <c>Math.Abs(int.MinValue)</c> โยน <see cref="OverflowException"/> (ค่าบวกของมันเกิน
    /// <see cref="int.MaxValue"/>) ซึ่งจะทำให้ <see cref="Parse"/> ที่ดักแค่ <see cref="JsonException"/>
    /// โยนทะลุขึ้นไปจน endpoint ตอบ 500 ทั้งที่ควรเป็น 400 ธรรมดา</summary>
    public static bool IsWithinBounds(GridPoint square) =>
        square.X >= -MaxCoordinate && square.X <= MaxCoordinate &&
        square.Y >= -MaxCoordinate && square.Y <= MaxCoordinate;

    /// <summary>
    /// พื้นทั้งหมดที่ส่งให้ engine ตอนคิดการเคลื่อนที่ = ชั้นแผนที่จาก <paramref name="terrainJson"/> +
    /// ชั้น effect ที่ **ยังเป็น <see cref="TerrainLayer.Empty"/> เสมอ** (ยังไม่ทำ — ดู doc ของคลาส)
    /// </summary>
    public static TerrainField ToTerrainField(string? terrainJson) =>
        new(Parse(terrainJson), TerrainLayer.Empty);
}

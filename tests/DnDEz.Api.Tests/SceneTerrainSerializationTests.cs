using System.Text;
using DnDEz.Api.Endpoints;
using DnDEz.Rules;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 5 — อ่าน/เขียน `scenes.terrain` (พื้นจากแผนที่ของ spatial-play-design §3.3)
/// เทสต์หน่วยล้วน ไม่แตะ DB · สเปกเต็มอยู่ใน doc comment ของ SceneTerrainSerialization
/// </summary>
public class SceneTerrainSerializationTests
{
    private static GridPoint P(int x, int y) => new(x, y);

    private static bool SameSquares(IReadOnlySet<GridPoint> actual, params GridPoint[] expected) =>
        actual.SetEquals(expected);

    // ── Parse: ไม่ไว้ใจข้อมูล ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("{\"difficult\":5,\"blocked\":\"x\"}")]
    public void Parse_of_missing_or_malformed_terrain_is_empty_and_never_throws(string? json)
    {
        var layer = SceneTerrainSerialization.Parse(json);

        Assert.Empty(layer.Difficult);
        Assert.Empty(layer.Blocked);
    }

    [Fact]
    public void Parse_reads_both_layers()
    {
        var layer = SceneTerrainSerialization.Parse("""{"difficult":[[1,0],[2,0]],"blocked":[[5,5]]}""");

        Assert.True(SameSquares(layer.Difficult, P(1, 0), P(2, 0)));
        Assert.True(SameSquares(layer.Blocked, P(5, 5)));
    }

    /// <summary>รายการผิดรูปข้ามทีละรายการ ไม่ทิ้งทั้งก้อน — ข้อมูลเสียจุดเดียวต้องไม่ลบกำแพงทั้งฉาก</summary>
    [Fact]
    public void Parse_skips_malformed_entries_one_by_one()
    {
        var layer = SceneTerrainSerialization.Parse(
            """{"difficult":[[1,0],[2],[3,4,5],"x",{"x":1},[1.5,2],[7,8]],"blocked":[]}""");

        Assert.True(SameSquares(layer.Difficult, P(1, 0), P(7, 8)));
    }

    [Fact]
    public void Parse_skips_squares_outside_the_coordinate_bounds()
    {
        var max = SceneTerrainSerialization.MaxCoordinate;
        var layer = SceneTerrainSerialization.Parse(
            $$"""{"difficult":[[{{max}},{{-max}}],[{{max + 1}},0],[0,{{-max - 1}}]],"blocked":[]}""");

        Assert.True(SameSquares(layer.Difficult, P(max, -max)));
    }

    /// <summary>ช่องที่ถูกเขียนลงทั้งสองเซ็ต (เช่นแก้ DB มือ) นับเป็นกำแพงอย่างเดียว</summary>
    [Fact]
    public void Parse_square_listed_in_both_layers_counts_as_blocked_only()
    {
        var layer = SceneTerrainSerialization.Parse("""{"difficult":[[1,1]],"blocked":[[1,1]]}""");

        Assert.Empty(layer.Difficult);
        Assert.True(SameSquares(layer.Blocked, P(1, 1)));
    }

    /// <summary>กันข้อมูลใหญ่ผิดปกติที่หลุดเข้า DB มาทางอื่น — อ่านไม่เกินเพดานทั้งฉาก</summary>
    [Fact]
    public void Parse_reads_at_most_the_scene_cap()
    {
        var cap = SceneTerrainSerialization.MaxSquaresPerScene;
        var json = new StringBuilder("{\"difficult\":[");
        for (var i = 0; i < cap + 5; i++)
        {
            if (i > 0) json.Append(',');
            json.Append('[').Append(i % 1000).Append(',').Append(i / 1000).Append(']');
        }
        json.Append("],\"blocked\":[]}");

        var layer = SceneTerrainSerialization.Parse(json.ToString());

        Assert.Equal(cap, layer.Difficult.Count + layer.Blocked.Count);
    }

    // ── Serialize ────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_is_deterministic_sorted_by_y_then_x()
    {
        var layer = TerrainLayer.FromSquares([P(2, 1), P(0, 1), P(5, 0)], [P(9, 9)]);

        Assert.Equal(
            """{"difficult":[[5,0],[0,1],[2,1]],"blocked":[[9,9]]}""",
            SceneTerrainSerialization.Serialize(layer));
    }

    [Fact]
    public void Serialize_writes_both_keys_even_when_empty()
    {
        Assert.Equal("""{"difficult":[],"blocked":[]}""", SceneTerrainSerialization.Serialize(TerrainLayer.Empty));
    }

    [Fact]
    public void Serialize_then_parse_round_trips()
    {
        var layer = TerrainLayer.FromSquares([P(-3, 4), P(0, 0)], [P(1, -1)]);

        var back = SceneTerrainSerialization.Parse(SceneTerrainSerialization.Serialize(layer));

        Assert.True(back.Difficult.SetEquals(layer.Difficult));
        Assert.True(back.Blocked.SetEquals(layer.Blocked));
    }

    /// <summary>
    /// พิกัดติดลบต้องเขียนด้วย ASCII '-' เสมอไม่ว่าเครื่องตั้ง locale อะไร — <c>StringBuilder.Append(int)</c>
    /// ใช้เครื่องหมายลบของ culture ปัจจุบัน และบาง locale (เช่น sv-SE บน ICU) ใช้ U+2212 ซึ่งทำให้ JSON ที่
    /// บันทึกลง DB พัง แล้ว <c>Parse</c> ครั้งถัดไปคืนชั้นว่าง → **กำแพงทั้งฉากหายเงียบ ๆ** · บั๊กตระกูลเดียวกับ
    /// ที่เจอใน ActionFormulaResolver (Sprint H Part 4) · owner จับได้ตอนอ่าน diff ของ worker
    /// </summary>
    [Fact]
    public void Serialize_writes_negative_coordinates_with_an_ascii_minus_regardless_of_culture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            var layer = TerrainLayer.FromSquares([P(-3, -4)], [P(-1, 2)]);

            var json = SceneTerrainSerialization.Serialize(layer);

            Assert.Equal("""{"difficult":[[-3,-4]],"blocked":[[-1,2]]}""", json);
            var back = SceneTerrainSerialization.Parse(json);
            Assert.True(SameSquares(back.Difficult, P(-3, -4)));
            Assert.True(SameSquares(back.Blocked, P(-1, 2)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // ── Apply: การระบายของ DM ──────────────────────────────────────────────

    [Fact]
    public void Apply_difficult_adds_squares()
    {
        var result = SceneTerrainSerialization.Apply("{}", TerrainPaint.Difficult, [P(1, 0), P(2, 0)]);

        Assert.True(result.Succeeded);
        Assert.True(SameSquares(result.Layer.Difficult, P(1, 0), P(2, 0)));
        Assert.Empty(result.Layer.Blocked);
        Assert.Equal("""{"difficult":[[1,0],[2,0]],"blocked":[]}""", result.Json);
    }

    /// <summary>ช่องเป็นได้อย่างเดียว — ระบายกำแพงทับพื้นเดินยาก ต้องย้ายช่องนั้นไปเป็นกำแพง</summary>
    [Fact]
    public void Apply_blocked_over_difficult_moves_the_square()
    {
        var result = SceneTerrainSerialization.Apply(
            """{"difficult":[[1,0],[2,0]],"blocked":[]}""", TerrainPaint.Blocked, [P(2, 0)]);

        Assert.True(SameSquares(result.Layer.Difficult, P(1, 0)));
        Assert.True(SameSquares(result.Layer.Blocked, P(2, 0)));
    }

    [Fact]
    public void Apply_difficult_over_blocked_moves_the_square()
    {
        var result = SceneTerrainSerialization.Apply(
            """{"difficult":[],"blocked":[[3,3]]}""", TerrainPaint.Difficult, [P(3, 3)]);

        Assert.True(SameSquares(result.Layer.Difficult, P(3, 3)));
        Assert.Empty(result.Layer.Blocked);
    }

    [Fact]
    public void Apply_clear_removes_from_both_layers()
    {
        var result = SceneTerrainSerialization.Apply(
            """{"difficult":[[1,0]],"blocked":[[2,0]]}""", TerrainPaint.Clear, [P(1, 0), P(2, 0), P(9, 9)]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Layer.Difficult);
        Assert.Empty(result.Layer.Blocked);
    }

    [Fact]
    public void Apply_same_square_twice_does_not_duplicate()
    {
        var once = SceneTerrainSerialization.Apply("{}", TerrainPaint.Blocked, [P(1, 1)]);
        var twice = SceneTerrainSerialization.Apply(once.Json, TerrainPaint.Blocked, [P(1, 1), P(1, 1)]);

        Assert.Equal("""{"difficult":[],"blocked":[[1,1]]}""", twice.Json);
    }

    /// <summary>พื้นเดิมใน DB พัง ต้องไม่ทำให้ DM ระบายไม่ได้ตลอดไป — ถือเป็นชั้นว่างแล้วระบายต่อ</summary>
    [Fact]
    public void Apply_on_malformed_current_terrain_starts_from_empty()
    {
        var result = SceneTerrainSerialization.Apply("[oops", TerrainPaint.Difficult, [P(0, 1)]);

        Assert.True(result.Succeeded);
        Assert.True(SameSquares(result.Layer.Difficult, P(0, 1)));
    }

    [Fact]
    public void Apply_exactly_up_to_the_scene_cap_succeeds()
    {
        var cap = SceneTerrainSerialization.MaxSquaresPerScene;
        var existing = Enumerable.Range(0, cap - 1).Select(i => P(i % 100, i / 100)).ToList();
        var current = SceneTerrainSerialization.Serialize(TerrainLayer.FromSquares(existing, []));

        var result = SceneTerrainSerialization.Apply(current, TerrainPaint.Blocked, [P(500, 500)]);

        Assert.True(result.Succeeded);
        Assert.Equal(cap, result.Layer.Difficult.Count + result.Layer.Blocked.Count);
    }

    /// <summary>เกินเพดานทั้งฉาก → ไม่สำเร็จ ไม่คืน JSON และคืนพื้นเดิมที่ไม่ถูกแตะ</summary>
    [Fact]
    public void Apply_beyond_the_scene_cap_fails_and_leaves_terrain_unchanged()
    {
        var cap = SceneTerrainSerialization.MaxSquaresPerScene;
        var existing = Enumerable.Range(0, cap).Select(i => P(i % 100, i / 100)).ToList();
        var current = SceneTerrainSerialization.Serialize(TerrainLayer.FromSquares(existing, []));

        var result = SceneTerrainSerialization.Apply(current, TerrainPaint.Blocked, [P(500, 500)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Json);
        Assert.Equal(cap, result.Layer.Difficult.Count);
        Assert.Empty(result.Layer.Blocked);
    }

    /// <summary>ย้ายช่องจากชั้นหนึ่งไปอีกชั้นไม่เพิ่มจำนวนรวม — ต้องไม่โดนเพดานทั้งที่ฉากเต็มพอดี</summary>
    [Fact]
    public void Apply_moving_squares_between_layers_at_the_cap_still_succeeds()
    {
        var cap = SceneTerrainSerialization.MaxSquaresPerScene;
        var existing = Enumerable.Range(0, cap).Select(i => P(i % 100, i / 100)).ToList();
        var current = SceneTerrainSerialization.Serialize(TerrainLayer.FromSquares(existing, []));

        var result = SceneTerrainSerialization.Apply(current, TerrainPaint.Blocked, [P(0, 0)]);

        Assert.True(result.Succeeded);
        Assert.True(SameSquares(result.Layer.Blocked, P(0, 0)));
    }

    // ── ขอบพิกัด + ชั้น effect ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1000, -1000, true)]
    [InlineData(1001, 0, false)]
    [InlineData(0, -1001, false)]
    public void IsWithinBounds_uses_the_coordinate_cap(int x, int y, bool expected)
    {
        Assert.Equal(expected, SceneTerrainSerialization.IsWithinBounds(P(x, y)));
    }

    /// <summary>
    /// ขอบของ int — <c>Math.Abs(int.MinValue)</c> โยน OverflowException (ไม่มีค่าบวกที่แทน −2147483648 ได้)
    /// ต้องตอบ false ไม่ใช่ throw · code review ของ Part นี้เป็นคนจับได้ (เทสต์ขอบเดิมลองแค่ 1000/1001)
    /// </summary>
    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, int.MinValue)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(0, int.MaxValue)]
    public void IsWithinBounds_is_false_at_the_int_extremes_and_never_throws(int x, int y)
    {
        Assert.False(SceneTerrainSerialization.IsWithinBounds(P(x, y)));
    }

    /// <summary>
    /// **ผลที่แรงกว่าตัวบั๊กเอง**: ถ้าค่า int.MinValue หลุดเข้า `scenes.terrain` ได้ (แก้ DB มือ, บั๊กในอนาคต)
    /// Parse จะโยน OverflowException ซึ่ง catch แค่ JsonException จับไม่ได้ → ทุกการอ่านพื้นของฉากนั้นพัง
    /// (reachable/preview/commit/GET) จนกว่าจะมีคนไปแก้ DB · ต้องข้ามรายการนั้นไปเฉย ๆ ตามสัญญา "ไม่ throw"
    /// </summary>
    [Fact]
    public void Parse_skips_an_int_min_value_entry_instead_of_throwing()
    {
        var layer = SceneTerrainSerialization.Parse("""{"difficult":[[-2147483648,0],[1,1]],"blocked":[[0,-2147483648]]}""");

        Assert.True(SameSquares(layer.Difficult, P(1, 1)));
        Assert.Empty(layer.Blocked);
    }

    /// <summary>ชั้น effect ยังไม่ทำใน Part นี้ — ต้องว่างเสมอ เทสต์นี้ล็อกข้อเท็จจริงไว้ให้คนที่มาทำชั้น
    /// effect รู้ว่าต้องมาแก้ที่ ToTerrainField ที่เดียว</summary>
    [Fact]
    public void ToTerrainField_takes_the_map_layer_and_leaves_the_effect_layer_empty()
    {
        var field = SceneTerrainSerialization.ToTerrainField("""{"difficult":[[1,0]],"blocked":[[2,0]]}""");

        Assert.True(field.IsDifficult(P(1, 0)));
        Assert.True(field.IsBlocked(P(2, 0)));
        Assert.Same(TerrainLayer.Empty, field.FromEffects);
    }
}

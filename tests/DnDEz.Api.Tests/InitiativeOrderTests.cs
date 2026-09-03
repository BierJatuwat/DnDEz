using DnDEz.Api.Encounters;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint F งานที่ 4 — กฎจัดลำดับ initiative แยกออกมาเป็น pure function โดยตั้งใจ
///
/// **ทำไมต้องแยกออกจาก endpoint**: การทอย 1d20 ใช้ CSPRNG จริง (ห้าม mock ทั้งโปรเจกต์) จึงบังคับให้
/// เต๋าออกมา "เสมอกัน" ผ่าน HTTP ไม่ได้ — ถ้าทดสอบ tie-break ผ่าน HTTP ต้องวน retry จนบังเอิญเสมอ
/// ซึ่งเป็นเทสต์ flaky ที่พิสูจน์กฎไม่ได้จริง · แยกกฎออกมาเป็นฟังก์ชันบริสุทธิ์แล้วทดสอบตรง ๆ
/// deterministic 100% (endpoint แค่เรียกใช้ — ลำดับที่ endpoint คืนถูกคุมด้วย
/// <c>EncounterEndpointsTests</c> ที่ใช้ modifier ห่างกันจนช่วงไม่ทับ)
///
/// **กฎ (ตัดสิน 2026-09-03 — เป็น MVP ไม่ใช่ 5e เป๊ะ)**:
///   1. `InitiativeTotal` มาก → ก่อน
///   2. เสมอ → `InitiativeModifier` มาก → ก่อน (ใกล้เคียงกฎบ้าน 5e ที่ใช้ Dex ตัดสิน)
///   3. ยังเสมอ → `CharacterId` น้อย → ก่อน (`Guid.CompareTo`)
///
/// ข้อ 3 ไม่ได้ "ยุติธรรม" ในเชิงเกม แต่**จำเป็น**: 5e จริงให้ DM ตัดสินหรือให้ผู้เล่นทอยแก้เสมอ
/// ซึ่งเป็น flow ที่ยังไม่มีในระบบนี้ · สิ่งที่ห้ามคือปล่อยให้ลำดับขึ้นกับ sort ที่ไม่ stable แล้วได้
/// ผลต่างกันทุกครั้งที่อ่าน — ผลลัพธ์ต้องกำหนดได้แน่นอนจากข้อมูลในตารางเท่านั้น
/// </summary>
public class InitiativeOrderTests
{
    private static Guid Id(int last) => new($"00000000-0000-0000-0000-{last:D12}");

    private static readonly Guid Low = Id(1);
    private static readonly Guid Mid = Id(2);
    private static readonly Guid High = Id(3);

    [Fact]
    public void Orders_by_initiative_total_descending()
    {
        var result = InitiativeOrder.Resolve(new[]
        {
            new InitiativeEntry(Low, InitiativeRoll: 5, InitiativeModifier: 1),   // 6
            new InitiativeEntry(Mid, InitiativeRoll: 20, InitiativeModifier: 0),  // 20
            new InitiativeEntry(High, InitiativeRoll: 10, InitiativeModifier: 2), // 12
        });

        Assert.Equal(new[] { Mid, High, Low }, result.Select(e => e.CharacterId).ToArray());
    }

    [Fact]
    public void Computes_initiative_total_as_roll_plus_modifier()
    {
        var entry = new InitiativeEntry(Low, InitiativeRoll: 13, InitiativeModifier: -4);

        Assert.Equal(9, entry.InitiativeTotal);
    }

    [Fact]
    public void Ties_on_total_are_broken_by_higher_modifier_first()
    {
        // ผลรวมเท่ากันทั้งคู่ (15) แต่ Mid มี modifier สูงกว่า → ต้องมาก่อน
        var result = InitiativeOrder.Resolve(new[]
        {
            new InitiativeEntry(Low, InitiativeRoll: 14, InitiativeModifier: 1),
            new InitiativeEntry(Mid, InitiativeRoll: 10, InitiativeModifier: 5),
        });

        Assert.Equal(new[] { Mid, Low }, result.Select(e => e.CharacterId).ToArray());
    }

    [Fact]
    public void Ties_on_total_and_modifier_are_broken_by_lower_character_id_first()
    {
        var result = InitiativeOrder.Resolve(new[]
        {
            new InitiativeEntry(High, InitiativeRoll: 12, InitiativeModifier: 3),
            new InitiativeEntry(Low, InitiativeRoll: 12, InitiativeModifier: 3),
            new InitiativeEntry(Mid, InitiativeRoll: 12, InitiativeModifier: 3),
        });

        Assert.Equal(new[] { Low, Mid, High }, result.Select(e => e.CharacterId).ToArray());
    }

    /// <summary>
    /// หัวใจของกฎข้อ 3 — ผลลัพธ์ต้องขึ้นกับ "ข้อมูล" ไม่ใช่ "ลำดับที่ส่งเข้ามา"
    /// (List.Sort ของ .NET เป็น introsort ที่ **ไม่ stable** ถ้าไม่ระบุ tiebreak ครบ ผลจะสลับได้จริง)
    /// </summary>
    [Fact]
    public void Result_is_identical_regardless_of_input_order()
    {
        // ทั้งสามใบผลรวมเท่ากันหมด (12+3 = 12+3 = 15+0 = 15) โดยตั้งใจ — บังคับให้ต้องใช้ tiebreak
        // ครบทั้งสองชั้นถึงจะได้ลำดับเดียวกันทุกครั้ง
        var entries = new[]
        {
            new InitiativeEntry(Low, InitiativeRoll: 12, InitiativeModifier: 3),
            new InitiativeEntry(Mid, InitiativeRoll: 12, InitiativeModifier: 3),
            new InitiativeEntry(High, InitiativeRoll: 15, InitiativeModifier: 0),
        };

        var forward = InitiativeOrder.Resolve(entries).Select(e => e.CharacterId).ToArray();
        var reversed = InitiativeOrder.Resolve(entries.Reverse()).Select(e => e.CharacterId).ToArray();

        Assert.Equal(forward, reversed);

        // แก้ค่าคาดหวังหลังรันจริง (2026-09-03) — ของเดิมเขียนเป็น { High, Low, Mid } ซึ่ง**ผิด spec
        // ที่ owner เขียนไว้เอง** ไม่ใช่ implementation ผิด: เผลอคิดว่า High (15+0) ผลรวมมากสุด
        // แต่จริง ๆ เสมอกันที่ 15 ทั้งสามใบ → กฎชั้น 2 (modifier มากก่อน) ดัน Low/Mid (mod 3)
        // ขึ้นก่อน High (mod 0) · แก้ตาม TDD ข้อ 5 (แก้ได้เมื่อเทสต์เขียนผิด spec จริง + บันทึกเหตุผล)
        Assert.Equal(new[] { Low, Mid, High }, forward);
    }

    [Fact]
    public void Preserves_every_entry_exactly_once()
    {
        var entries = new[]
        {
            new InitiativeEntry(Low, InitiativeRoll: 1, InitiativeModifier: 0),
            new InitiativeEntry(Mid, InitiativeRoll: 1, InitiativeModifier: 0),
            new InitiativeEntry(High, InitiativeRoll: 20, InitiativeModifier: 0),
        };

        var result = InitiativeOrder.Resolve(entries);

        Assert.Equal(3, result.Count);
        Assert.Equal(entries.OrderBy(e => e.CharacterId), result.OrderBy(e => e.CharacterId));
    }

    [Fact]
    public void Empty_input_returns_empty_result()
    {
        Assert.Empty(InitiativeOrder.Resolve(Array.Empty<InitiativeEntry>()));
    }

    [Fact]
    public void Single_entry_is_returned_as_is()
    {
        var result = InitiativeOrder.Resolve(new[]
        {
            new InitiativeEntry(Mid, InitiativeRoll: 7, InitiativeModifier: -2),
        });

        Assert.Single(result);
        Assert.Equal(Mid, result[0].CharacterId);
        Assert.Equal(5, result[0].InitiativeTotal);
    }

    [Fact]
    public void Negative_totals_still_sort_correctly()
    {
        var result = InitiativeOrder.Resolve(new[]
        {
            new InitiativeEntry(Low, InitiativeRoll: 1, InitiativeModifier: -20),  // -19
            new InitiativeEntry(Mid, InitiativeRoll: 1, InitiativeModifier: 0),    // 1
            new InitiativeEntry(High, InitiativeRoll: 1, InitiativeModifier: -10), // -9
        });

        Assert.Equal(new[] { Mid, High, Low }, result.Select(e => e.CharacterId).ToArray());
    }
}

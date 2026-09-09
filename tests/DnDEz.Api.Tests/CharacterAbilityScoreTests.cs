using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 1 — STR/DEX/CON/INT/WIS/CHA + speed เป็น typed column ใน `characters`
/// (ก่อนหน้านี้ไม่มีเลย ดู design doc §2.5 — ค่าโจมตี "+5" ไม่มีที่มาเพราะไม่มีค่าความสามารถให้อ้างอิง)
///
/// ใช้เลนส์เดียวกับ HP/AC (Sprint B Part 2, migration `AddCharacterCombatStats`): "ทุกตัวละครมีแน่นอน
/// ไม่ผันแปรตามคลาส" → typed column ไม่ใช่ jsonb · migration ต้องมี default ที่ตัวละครเดิมไม่พัง
/// (ตรวจผ่าน information_schema.columns.column_default โดยตรง ไม่ insert จริง เพราะ `characters`
/// มี NOT NULL column อื่นอีกหลายตัวที่ผูกกับ campaign/session ทำให้ insert ตรง ๆ ในเทสต์นี้ซับซ้อนเกินจำเป็น
/// — ยืนยัน default ที่ระดับ schema ก็พอสำหรับ DoD ของ Part นี้ "ตัวละครเดิมไม่พัง")
/// </summary>
public class CharacterAbilityScoreTests
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    // ค่าเริ่มต้น 10 = ability score เฉลี่ย (modifier +0) — ค่ากลางที่ไม่ทำให้ตัวละครเดิมได้เปรียบ/
    // เสียเปรียบผิดปกติ จนกว่าเจ้าของจะกรอกค่าจริง ใช้เลนส์เดียวกับ AddCharacterCombatStats ที่ใส่
    // MaxHp=10/AC=10 เป็น "safe placeholder" ไม่ใช่ค่าที่ถูกต้องของตัวละครจริง
    [Theory]
    [InlineData("strength")]
    [InlineData("dexterity")]
    [InlineData("constitution")]
    [InlineData("intelligence")]
    [InlineData("wisdom")]
    [InlineData("charisma")]
    public async Task Character_has_typed_ability_score_column_defaulting_to_10(string column)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'characters' AND column_name = @col
            """, conn);
        cmd.Parameters.AddWithValue("col", column);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"characters.{column} ไม่มีอยู่จริง — ต้องเพิ่มเป็น typed column");
        Assert.Equal("integer", reader.GetString(0));
        Assert.Equal("NO", reader.GetString(1));
        Assert.False(reader.IsDBNull(2), $"characters.{column} ต้องมี default ให้ตัวละครเดิมไม่พัง");
        Assert.StartsWith("10", reader.GetString(2));
    }

    // speed เดินมาตรฐานของมนุษย์/เผ่าพันธุ์ส่วนใหญ่ใน SRD = 30 ฟุต — ตอนนี้ hardcode อยู่ใน
    // EncounterEndpoints.DefaultMovementFeet พร้อม comment ยอมรับว่าชั่วคราว (Part นี้ทำให้มีที่มาจริง
    // แต่ยังไม่แก้จุดนั้น รอ Sprint G Part 8)
    [Fact]
    public async Task Character_has_typed_speed_column_defaulting_to_30_feet()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'characters' AND column_name = 'speed_feet'
            """, conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "characters.speed_feet ไม่มีอยู่จริง — ต้องเพิ่มเป็น typed column");
        Assert.Equal("integer", reader.GetString(0));
        Assert.Equal("NO", reader.GetString(1));
        Assert.False(reader.IsDBNull(2));
        Assert.StartsWith("30", reader.GetString(2));
    }
}

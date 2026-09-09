using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 1 — ยืนยันว่าค่าที่ seed ลง `action_catalog_entries` ตรงกับที่ตั้งใจจริง (ไม่ใช่แค่
/// "seed ได้ไม่ error") — ค่า SRD ด้านล่างเป็นค่าที่ owner ยืนยันเองก่อน dispatch ตาม CLAUDE.md ข้อ 4a/
/// "ห้ามให้ haiku-worker เขียนตัวเลขกฎที่ไม่ได้วัดจริง" คลังนี้ seed เฉพาะฟิลด์ที่ engine ใช้จริง
/// (design doc §2.3: ระยะ/ชนิดเป้า/ราคา/รูปทอย) ไม่ใช่ damage dice หรือคำบรรยาย (อยู่ jsonb `details`
/// แยกต่างหาก ไม่ใช่ขอบเขตของ Part นี้)
///
/// ค่าที่ยืนยัน (SRD 5e/5.2, ตัวเลขพื้นฐานที่ไม่เปลี่ยนข้ามฉบับ):
///   - Longsword: อาวุธ martial melee, ระยะประชิด 5 ฟุต, ทอย vs AC
///   - Shortbow: อาวุธ simple ranged, ระยะ 80/320 ฟุต, ทอย vs AC
///   - Fire Bolt: cantrip (spell_slot_level=0) โจมตีเวทระยะไกล 120 ฟุต, ทอย vs AC
///   - Magic Missile: เวทระดับ 1, ระยะ 120 ฟุต, **auto-hit ไม่มีทั้งทอยโจมตีและ save** (roll_kind=None)
/// </summary>
public class ActionCatalogSeedDataTests
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private async Task<NpgsqlDataReader> QueryEntryAsync(NpgsqlConnection conn, string key)
    {
        var cmd = new NpgsqlCommand(
            """
            SELECT kind, action_cost, target_kind, range_normal_feet, range_long_feet,
                   roll_kind, spell_slot_level, rules_system
            FROM action_catalog_entries WHERE key = @key
            """, conn);
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteReaderAsync();
    }

    [Fact]
    public async Task Longsword_is_seeded_as_melee_weapon_with_attack_roll()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var reader = await QueryEntryAsync(conn, "longsword");

        Assert.True(await reader.ReadAsync(), "ไม่พบ entry key='longsword' — seed ยังไม่ครบ");
        Assert.Equal("Weapon", reader.GetString(0));
        Assert.Equal("Action", reader.GetString(1));
        Assert.Equal("Enemy", reader.GetString(2));
        Assert.Equal(5, reader.GetInt32(3));
        Assert.True(reader.IsDBNull(4), "longsword เป็นอาวุธประชิดล้วน ไม่มีระยะไกล");
        Assert.Equal("AttackRoll", reader.GetString(5));
        Assert.True(reader.IsDBNull(6), "อาวุธไม่ใช่เวท ต้องไม่มี spell_slot_level");
        Assert.Equal("srd2024", reader.GetString(7));
    }

    [Fact]
    public async Task Shortbow_is_seeded_as_ranged_weapon_with_normal_and_long_range()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var reader = await QueryEntryAsync(conn, "shortbow");

        Assert.True(await reader.ReadAsync(), "ไม่พบ entry key='shortbow' — seed ยังไม่ครบ");
        Assert.Equal("Weapon", reader.GetString(0));
        Assert.Equal("Action", reader.GetString(1));
        Assert.Equal("Enemy", reader.GetString(2));
        Assert.Equal(80, reader.GetInt32(3));
        Assert.Equal(320, reader.GetInt32(4));
        Assert.Equal("AttackRoll", reader.GetString(5));
    }

    [Fact]
    public async Task Fire_bolt_is_seeded_as_cantrip_with_120ft_range_and_attack_roll()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var reader = await QueryEntryAsync(conn, "fire-bolt");

        Assert.True(await reader.ReadAsync(), "ไม่พบ entry key='fire-bolt' — seed ยังไม่ครบ");
        Assert.Equal("Spell", reader.GetString(0));
        Assert.Equal("Action", reader.GetString(1));
        Assert.Equal("Enemy", reader.GetString(2));
        Assert.Equal(120, reader.GetInt32(3));
        Assert.True(reader.IsDBNull(4));
        Assert.Equal("AttackRoll", reader.GetString(5));
        Assert.Equal(0, reader.GetInt32(6)); // cantrip = spell_slot_level 0
    }

    [Fact]
    public async Task Magic_missile_is_seeded_as_level1_spell_with_no_roll_auto_hit()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var reader = await QueryEntryAsync(conn, "magic-missile");

        Assert.True(await reader.ReadAsync(), "ไม่พบ entry key='magic-missile' — seed ยังไม่ครบ");
        Assert.Equal("Spell", reader.GetString(0));
        Assert.Equal("Action", reader.GetString(1));
        Assert.Equal(120, reader.GetInt32(3));
        Assert.Equal("None", reader.GetString(5)); // auto-hit — ไม่ทอยโจมตี ไม่มี save
        Assert.Equal(1, reader.GetInt32(6));
    }

    // ── การเคลื่อนที่ (design doc §3.2 — "แตะพื้นหรือไม่" คือฟิลด์หัวใจของ Part นี้) ──────────
    // ⚠️ ค่า touches_ground ด้านล่างอ้างอิงการตัดสินใจเชิงโครงสร้างใน design doc §3.2 โดยตรง
    // ("กระโดดข้าม/ปีนข้าม → ไม่คิดผลพื้น") ไม่ใช่ตัวเลข/กลไกที่ตรวจกับ SRD 5.2 แล้ว — เอกสารเขียน
    // ไว้ชัดว่า "รายละเอียดกฎจริงยังไม่ได้ตรวจ ที่ตกลงกันวันนี้คือโครงสร้าง ไม่ใช่ตัวเลข" ดังนั้นเทสต์นี้
    // ยืนยันแค่โครงสร้าง (touches_ground true/false ตามที่ออกแบบ) ไม่ยืนยันตัวเลขต้นทุนการเคลื่อนที่
    // ใด ๆ (นั่นคืองานของ Sprint G Part 8)
    [Theory]
    [InlineData("walk", true)]
    [InlineData("jump", false)]
    [InlineData("climb", false)]
    public async Task Movement_entry_declares_touches_ground_per_design_doc_section_3_2(
        string key, bool expectedTouchesGround)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT kind, action_cost, roll_kind, touches_ground FROM action_catalog_entries WHERE key = @key",
            conn);
        cmd.Parameters.AddWithValue("key", key);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"ไม่พบ entry key='{key}' — seed ยังไม่ครบ");
        Assert.Equal("Movement", reader.GetString(0));
        Assert.Equal("None", reader.GetString(1)); // ไม่ใช่ action economy — มาจาก movement pool
        Assert.Equal("None", reader.GetString(2)); // ไม่มีการทอย
        Assert.False(reader.IsDBNull(3), $"{key}.touches_ground ต้องไม่เป็น null (มีความหมายเฉพาะ Movement)");
        Assert.Equal(expectedTouchesGround, reader.GetBoolean(3));
    }
}

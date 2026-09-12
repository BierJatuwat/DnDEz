using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 4 — ยืนยันว่าดาเมจ/ability ที่ seed ลง `action_catalog_entries` ตรงกับตัวบท SRD 5.2
/// ฉบับ CC (SRD_CC_v5.2.pdf, `pdftotext -raw`) — owner เปิดตัวบทเองทุกตัวก่อนเขียนเทสต์นี้:
///
///   - ตาราง Weapons: *"Longsword 1d8 Slashing Versatile (1d10)"* · *"Shortbow 1d6 Piercing
///     Ammunition (Range 80/320; Arrow), Two-Handed"* · *"Greataxe 1d12 Slashing Heavy, Two-Handed"*
///   - Fire Bolt: *"Make a ranged spell attack ... the target takes 1d10 Fire damage."*
///   - Magic Missile: *"three glowing darts ... A dart deals 1d4 + 1 Force damage"*
///   - Sacred Flame: *"must succeed on a Dexterity saving throw or take 1d8 Radiant damage."*
///   - Cure Wounds: *"regains a number of Hit Points equal to 2d8 plus your spellcasting ability
///     modifier."*
///   - อาวุธ: *"Melee attack bonus = Strength modifier ..."* · *"Ranged attack bonus = Dexterity
///     modifier ..."* · *"You add the same ability modifier you use for attacks with a weapon to
///     your damage rolls with that weapon."*
///   - เวท: *"A spell tells you which dice to roll for damage and whether to add any modifiers."*
///
/// **ดาเมจที่ผิดหลอกผู้เล่นได้เงียบ ๆ** (ไม่มีใครจำได้ว่าดาบยาวควรเป็น 1d8) — เทสต์นี้จึงเป็นตัวจับ
/// เดียวที่มี ถ้าวันหนึ่ง seed เพี้ยน
/// </summary>
public class ActionCatalogDamageSeedTests
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    /// <summary>
    /// Magic Missile เก็บเป็น "3d4+3" = ลูกดอก 3 ลูก × (1d4 + 1) ที่ยิงเข้าเป้าเดียว — affordance เป็นคู่
    /// (ท่า, เป้า) หนึ่งบรรทัดจึงหมายถึง "ยิงทั้งสามลูกใส่ตัวนี้" การกระจายลูกดอกไปหลายเป้ายังไม่ได้ทำโมเดล
    ///
    /// Longsword เก็บเป็น 1d8 (ถือมือเดียว) ไม่ใช่ 1d10 ของ Versatile — preset นักรบถือโล่ มือจึงไม่ว่าง
    /// </summary>
    [Theory]
    [InlineData("longsword", "1d8", "Slashing", null, "Strength", true)]
    [InlineData("shortbow", "1d6", "Piercing", null, "Dexterity", true)]
    [InlineData("greataxe", "1d12", "Slashing", null, "Strength", true)]
    [InlineData("fire-bolt", "1d10", "Fire", null, "Spellcasting", false)]
    [InlineData("magic-missile", "3d4+3", "Force", null, null, false)]
    [InlineData("sacred-flame", "1d8", "Radiant", null, "Spellcasting", false)]
    [InlineData("cure-wounds", null, null, "2d8", "Spellcasting", true)]
    public async Task Damage_and_ability_match_the_srd_text(
        string key, string? damageDice, string? damageType, string? healingDice, string? ability,
        bool addsAbilityToEffect)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT damage_dice, damage_type, healing_dice, ability, adds_ability_to_effect
            FROM action_catalog_entries WHERE key = @key AND rules_system = 'srd2024'
            """, conn);
        cmd.Parameters.AddWithValue("key", key);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"ไม่พบ entry key='{key}'");
        Assert.Equal(damageDice, reader.IsDBNull(0) ? null : reader.GetString(0));
        Assert.Equal(damageType, reader.IsDBNull(1) ? null : reader.GetString(1));
        Assert.Equal(healingDice, reader.IsDBNull(2) ? null : reader.GetString(2));
        Assert.Equal(ability, reader.IsDBNull(3) ? null : reader.GetString(3));
        Assert.Equal(addsAbilityToEffect, reader.GetBoolean(4));
    }

    [Theory]
    [InlineData("walk")]
    [InlineData("jump")]
    [InlineData("climb")]
    public async Task Movement_entries_have_no_damage_healing_or_ability(string key)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT damage_dice IS NULL AND damage_type IS NULL AND healing_dice IS NULL
                   AND ability IS NULL AND NOT adds_ability_to_effect
            FROM action_catalog_entries WHERE key = @key AND rules_system = 'srd2024'
            """, conn);
        cmd.Parameters.AddWithValue("key", key);

        Assert.True((bool)(await cmd.ExecuteScalarAsync())!, $"{key} ต้องไม่มีดาเมจ/การรักษา/ability");
    }

    /// <summary>ทุกสูตรใน catalog ต้อง parse ได้ด้วย parser ตัวเดียวกับที่ endpoint โจมตีใช้ — กัน seed
    /// ในอนาคตที่พิมพ์ผิดรูป (เช่น "1d8 + 2" หรือ "d8") ซึ่งจะไปพังตอนผู้เล่นกดโจมตีจริง</summary>
    [Fact]
    public async Task Every_dice_formula_in_the_catalog_parses()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT key, damage_dice FROM action_catalog_entries WHERE damage_dice IS NOT NULL
            UNION ALL
            SELECT key, healing_dice FROM action_catalog_entries WHERE healing_dice IS NOT NULL
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var count = 0;
        while (await reader.ReadAsync())
        {
            var formula = reader.GetString(1);
            var ex = Record.Exception(() => DnDEz.Rules.DiceExpressionParser.Parse(formula));
            Assert.True(ex is null, $"{reader.GetString(0)}: สูตร '{formula}' parse ไม่ได้ — {ex?.Message}");
            count++;
        }

        Assert.True(count >= 7, $"คาดว่ามีสูตรอย่างน้อย 7 ตัว (6 ดาเมจ + 1 รักษา) แต่เจอ {count}");
    }

    /// <summary>
    /// ด้านพฤติกรรมของ CHECK: รับ **ทุกค่า** ของ <c>DnDEz.Rules.DamageType</c> (ถ้า Rules เพิ่มชนิดใหม่
    /// แต่ลืมแก้ CHECK เทสต์นี้แดง) และปฏิเสธค่าที่ไม่ใช่ damage type จริง (ตัวอย่าง "Healing")
    ///
    /// ⚠️ เทสต์นี้พิสูจน์ได้ **ทางเดียว** — ชื่อเกินที่แอบอยู่ใน CHECK (เช่นพิมพ์ผิดเป็น 'Sonic') จะไม่ถูกจับ
    /// เพราะลองปฏิเสธแค่ค่าเดียว · อีกทางพิสูจน์ด้วย <see cref="Damage_type_check_lists_exactly_the_rules_damage_types"/>
    /// (code review รอบ 2 ของ Part นี้ชี้ว่า doc เดิมเขียนเกินจริงว่า "พอดี")
    ///
    /// ทุกอย่างอยู่ใน transaction ที่ rollback — ไม่แตะข้อมูลจริง
    /// </summary>
    [Fact]
    public async Task Damage_type_check_accepts_exactly_the_rules_damage_types()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        foreach (var name in Enum.GetNames<DnDEz.Rules.DamageType>())
        {
            await using var ok = new NpgsqlCommand(
                "UPDATE action_catalog_entries SET damage_type = @t WHERE key = 'longsword'", conn, tx);
            ok.Parameters.AddWithValue("t", name);
            await ok.ExecuteNonQueryAsync();
        }

        await using var bad = new NpgsqlCommand(
            "UPDATE action_catalog_entries SET damage_type = 'Healing' WHERE key = 'longsword'", conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        await tx.RollbackAsync();
    }

    /// <summary>
    /// ด้านโครงสร้างของ CHECK: รายชื่อใน constraint จริงบน DB ต้อง **เท่ากับ** ชื่อใน
    /// <c>DnDEz.Rules.DamageType</c> แบบเซตต่อเซต — จับได้ทั้งสองทาง: ชื่อที่ Rules มีแต่ CHECK ขาด และชื่อ
    /// เกินที่แอบอยู่ใน CHECK (เช่นพิมพ์ผิด) ซึ่งเทสต์ด้านพฤติกรรมข้างบนพิสูจน์ไม่ได้
    ///
    /// อ่านจาก <c>pg_get_constraintdef</c> ของ constraint ที่ apply แล้วจริง ไม่ใช่จากไฟล์ migration —
    /// สิ่งที่บังคับใช้คือของบน DB
    /// </summary>
    [Fact]
    public async Task Damage_type_check_lists_exactly_the_rules_damage_types()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
            WHERE conname = 'ck_damage_type_actioncatalogentry'
              AND conrelid = 'action_catalog_entries'::regclass
            """, conn);
        var definition = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(definition);

        var inCheck = System.Text.RegularExpressions.Regex.Matches(definition!, "'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        var inRules = Enum.GetNames<DnDEz.Rules.DamageType>().ToHashSet();

        Assert.True(inCheck.SetEquals(inRules),
            $"CHECK ขาด: [{string.Join(", ", inRules.Except(inCheck))}] · "
            + $"CHECK เกิน: [{string.Join(", ", inCheck.Except(inRules))}]");
    }

    /// <summary>ดาเมจที่ไม่มีชนิด = หัก resistance ไม่ได้ → DB ต้องปฏิเสธตั้งแต่ต้นทาง</summary>
    [Fact]
    public async Task Damage_dice_without_a_damage_type_is_rejected()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE action_catalog_entries SET damage_type = NULL WHERE key = 'longsword'", conn, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Ability_outside_the_enum_is_rejected()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE action_catalog_entries SET ability = 'Intelligence' WHERE key = 'longsword'", conn, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        await tx.RollbackAsync();
    }

    /// <summary>
    /// **ช่องที่เจอระหว่างทำ Part นี้**: Part 1 seed 7 แถวแรกด้วย <c>gen_random_uuid()</c> แต่ preset
    /// ของ Part 7 hardcode id ที่บังเอิญตรงกับ DB เครื่อง dev เท่านั้น — migrate ฐานใหม่เอี่ยมแล้ว
    /// ตรวจจริง (ฐาน scratch `dndez_fresh_probe`, 2026-09-11) พบว่า ref ของ preset **5 จาก 8 ตัว
    /// ชี้ไปที่ไม่มีอยู่** (นักรบ, จอมเวทย์ ×2, จอมโจร, เรนเจอร์) → ตัวละครจาก preset เหล่านั้นได้
    /// affordance ว่างเปล่า
    ///
    /// เทสต์นี้เป็น invariant ถาวร — บน DB dev มันผ่านอยู่แล้วโดยบังเอิญ จึงพิสูจน์ช่องโหว่ไม่ได้ด้วย
    /// ตัวเอง หลักฐาน red-before-green อยู่ที่การ migrate ฐานใหม่ (ดู `.claude/logs/routing.md`)
    /// </summary>
    [Fact]
    public async Task Every_preset_action_ref_points_at_a_real_catalog_entry()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.name, r.ref
            FROM character_presets p
            CROSS JOIN LATERAL jsonb_array_elements_text(p.build -> 'actionCatalogRefs') AS r(ref)
            WHERE NOT EXISTS (SELECT 1 FROM action_catalog_entries e WHERE e.id::text = r.ref)
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var dangling = new List<string>();
        while (await reader.ReadAsync())
        {
            dangling.Add($"{reader.GetString(0)} → {reader.GetString(1)}");
        }

        Assert.True(dangling.Count == 0, "ref ของ preset ชี้ไปที่ไม่มีอยู่: " + string.Join(", ", dangling));
    }

    /// <summary>id ของ 4 แถวที่ preset อ้างถึงต้องคงที่ทุกเครื่อง — ตรงกับค่าที่ Part 7 hardcode ไว้</summary>
    [Theory]
    [InlineData("longsword", "794fe6ea-b50c-4f74-b66b-eb2455c10301")]
    [InlineData("shortbow", "8d52c9fc-8f3d-4405-bd71-f2bf09aa017b")]
    [InlineData("fire-bolt", "86636c2c-1c6b-4488-a5f5-f3ea56f984ba")]
    [InlineData("magic-missile", "2d864a4f-130f-4375-9063-70863b3d4774")]
    public async Task Catalog_rows_referenced_by_presets_have_fixed_ids(string key, string expectedId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM action_catalog_entries WHERE key = @key AND rules_system = 'srd2024'", conn);
        cmd.Parameters.AddWithValue("key", key);

        Assert.Equal(Guid.Parse(expectedId), (Guid)(await cmd.ExecuteScalarAsync())!);
    }
}

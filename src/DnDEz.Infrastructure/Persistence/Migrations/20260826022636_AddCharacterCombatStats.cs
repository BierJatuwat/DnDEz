using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint B Part 2 — ตัดสินใจ character sheet ↔ rules engine mapping (ดู PROJECT-CONTEXT §4)
    ///
    /// promote เฉพาะตัวเลขที่ DnDEz.Rules ต้องใช้ตรง ๆ ในการคำนวณ combat ออกจาก `characters.sheet`
    /// (jsonb) มาเป็น typed column: HP (current/max/temp), AC, resistance/vulnerability/immunity —
    /// เหตุผลคือ "ทุกตัวละครมีแน่นอน ไม่ผันแปรตามคลาส/subclass" ต่างจากของที่เหลือใน Sheet ที่ผันแปรจริง
    /// (spell list, inventory, feature text ฯลฯ ยังอยู่ใน jsonb เหมือนเดิม — ไม่ได้ยกเลิก sheet)
    ///
    /// ประโยชน์ที่ได้: (1) DB บังคับ invariant ได้ตรง ๆ ผ่าน CHECK constraint (HP ไม่ติดลบ/ไม่เกิน max
    /// เหมือน `ck_character_resources_range` ที่มีอยู่แล้วสำหรับ resource อื่น) (2) API อ่าน-เขียนตรง ๆ
    /// ไม่ต้อง parse/serialize jsonb ทั้งก้อนทุกครั้งที่ resolve การโจมตี ตรงกับ Sprint B Part 3 ที่จะเรียก
    /// DnDEz.Rules.HitPointResolver ต้องการ currentHp/maxHp/currentTempHp เป็น int ตรง ๆ
    ///
    /// ค่าเริ่มต้น (defaultValue) ใน Up() เป็น "safe placeholder" ไม่ใช่ค่าที่ถูกต้องสำหรับตัวละครจริง
    /// (เช่น MaxHp=10, AC=10) — ตัวละครใหม่ที่สร้างผ่าน POST /campaigns/{id}/characters (Sprint A
    /// Part 3, แก้เพิ่มในคอมมิตนี้) รับค่าจาก request ได้ ถ้าไม่ส่งมาค่อย fallback มาใช้ placeholder นี้
    /// </summary>
    public partial class AddCharacterCombatStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "armor_class",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "current_hp",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "current_temp_hp",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string[]>(
                name: "damage_immunities",
                table: "characters",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "damage_resistances",
                table: "characters",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "damage_vulnerabilities",
                table: "characters",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<int>(
                name: "max_hp",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            // ── CHECK constraint — บังคับ invariant ที่ชั้น DB เหมือนตารางอื่นในระบบนี้ ──────────
            migrationBuilder.Sql("""
                ALTER TABLE characters ADD CONSTRAINT ck_characters_max_hp CHECK (max_hp >= 1);
                ALTER TABLE characters ADD CONSTRAINT ck_characters_current_hp
                    CHECK (current_hp >= 0 AND current_hp <= max_hp);
                ALTER TABLE characters ADD CONSTRAINT ck_characters_current_temp_hp
                    CHECK (current_temp_hp >= 0);
                ALTER TABLE characters ADD CONSTRAINT ck_characters_armor_class
                    CHECK (armor_class >= 1 AND armor_class <= 50);

                ALTER TABLE characters ADD CONSTRAINT ck_characters_damage_resistances CHECK (
                    damage_resistances <@ ARRAY[
                        'Acid','Bludgeoning','Cold','Fire','Force','Lightning','Necrotic',
                        'Piercing','Poison','Psychic','Radiant','Slashing','Thunder'
                    ]::text[]
                );
                ALTER TABLE characters ADD CONSTRAINT ck_characters_damage_vulnerabilities CHECK (
                    damage_vulnerabilities <@ ARRAY[
                        'Acid','Bludgeoning','Cold','Fire','Force','Lightning','Necrotic',
                        'Piercing','Poison','Psychic','Radiant','Slashing','Thunder'
                    ]::text[]
                );
                ALTER TABLE characters ADD CONSTRAINT ck_characters_damage_immunities CHECK (
                    damage_immunities <@ ARRAY[
                        'Acid','Bludgeoning','Cold','Fire','Force','Lightning','Necrotic',
                        'Piercing','Poison','Psychic','Radiant','Slashing','Thunder'
                    ]::text[]
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE characters DROP CONSTRAINT IF EXISTS ck_characters_max_hp;
                ALTER TABLE characters DROP CONSTRAINT IF EXISTS ck_characters_current_hp;
                ALTER TABLE characters DROP CONSTRAINT IF EXISTS ck_characters_current_temp_hp;
                ALTER TABLE characters DROP CONSTRAINT IF EXISTS ck_characters_armor_class;
                ALTER TABLE characters DROP CONSTRAINT IF EXISTS ck_characters_damage_resistances;
                ALTER TABLE characters DROP CONSTRAINT IF EXISTS ck_characters_damage_vulnerabilities;
                ALTER TABLE characters DROP CONSTRAINT IF EXISTS ck_characters_damage_immunities;
                """);

            migrationBuilder.DropColumn(
                name: "armor_class",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "current_hp",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "current_temp_hp",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "damage_immunities",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "damage_resistances",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "damage_vulnerabilities",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "max_hp",
                table: "characters");
        }
    }
}

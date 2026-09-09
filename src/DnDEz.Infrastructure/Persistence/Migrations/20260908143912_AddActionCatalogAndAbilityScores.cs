using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint G Part 1 — คลังกลไก (action catalog) + ค่าความสามารถ 6 ค่า/speed ใน `characters`
    /// (design doc §2.3-§2.5) ดูรายละเอียดเหตุผลเต็มใน doc comment ของ `ActionCatalogEntry` และ
    /// `Character` (src/DnDEz.Domain/Entities/Campaigns.cs)
    ///
    /// ค่าเริ่มต้นของ ability score/speed (defaultValue ด้านล่าง) เป็น "safe placeholder" ตามเลนส์
    /// เดียวกับ `AddCharacterCombatStats` (MaxHp/AC=10) ไม่ใช่ค่าที่ถูกต้องของตัวละครจริง — 10 คือ
    /// ability score เฉลี่ย (modifier +0), 30 คือ speed เดินมาตรฐานส่วนใหญ่ใน SRD — EF generate
    /// `defaultValue: 0` มาให้ตอน `dotnet ef migrations add` (ไม่มี Fluent `HasDefaultValue` ใน
    /// DbContext ตามธรรมเนียมเดิมของโปรเจกต์) แก้เป็น 10/30 ด้วยมือตรงนี้
    ///
    /// ⚠️ **RLS ของ `action_catalog_entries` เข้มกว่า `rules_corpus_entries`** — ตารางเดิมมี comment
    /// "อ่านได้ทุกคน ไม่ต้องมี RLS" (migration `RowLevelSecurity`) เพราะเป็นแค่คลังข้อความ (tooltip)
    /// แต่ตารางนี้เป็น**ฐานตัวเลขที่ engine เชื่อถือเพื่อคำนวณ** (ระยะ/ราคา/รูปทอย) — ถ้า role แอป
    /// เขียนทับได้ ผู้เล่นจะโดนกฎที่ผิดเงียบ ๆ โดยไม่มีอะไรจับ ตัดสินใจ (owner, ยังไม่ apply ตอนเขียน
    /// comment นี้ — ดูผลจริงใน `.claude/logs/routing.md` หลัง apply): **อ่านได้ทุกคนที่ authenticate
    /// เขียนไม่ได้เลยแม้แต่ role แอป** แก้ไขคลังทำผ่าน migration (role เจ้าของ) เท่านั้น
    ///  - `ALTER DEFAULT PRIVILEGES` ใน migration `RowLevelSecurity` grant SELECT/INSERT/UPDATE/
    ///    DELETE ให้ `dndez_app` ตรง ๆ (ไม่ผ่าน PUBLIC) ⇒ REVOKE ตรงจาก `dndez_app` ใช้ได้ผลจริง
    ///    ต่างจากกับดัก CVE-2018-1058 ที่ REVOKE ต้องเล็ง PUBLIC (นั่นเป็นเรื่อง TEMPORARY ระดับ
    ///    DATABASE ที่ grant ให้ PUBLIC คนละ context กับที่นี่)
    ///  - RLS (ENABLE/FORCE + policy `read_only` แบบ `USING (true)`) เป็นชั้นที่สอง (defense in
    ///    depth) กันไว้เผื่อ GRANT ถูกคืนสิทธิ์ในอนาคตโดยไม่ตั้งใจ — เทสต์ยืนยันทั้งสองชั้นแยกกัน
    ///    (`Table_has_at_least_one_policy` กับ `App_role_has_select_only_on_action_catalog_entries`)
    /// </summary>
    public partial class AddActionCatalogAndAbilityScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "charisma",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "constitution",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "dexterity",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "intelligence",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "speed_feet",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "strength",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "wisdom",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateTable(
                name: "action_catalog_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rules_system = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    action_cost = table.Column<string>(type: "text", nullable: false),
                    spell_slot_level = table.Column<int>(type: "integer", nullable: true),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    range_normal_feet = table.Column<int>(type: "integer", nullable: true),
                    range_long_feet = table.Column<int>(type: "integer", nullable: true),
                    roll_kind = table.Column<string>(type: "text", nullable: false),
                    save_ability = table.Column<string>(type: "text", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    touches_ground = table.Column<bool>(type: "boolean", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_catalog_entries", x => x.id);
                    table.CheckConstraint("ck_action_cost_actioncatalogentry", "action_cost IN ('Action', 'BonusAction', 'Reaction', 'None')");
                    table.CheckConstraint("ck_kind_actioncatalogentry", "kind IN ('Weapon', 'Spell', 'Movement')");
                    table.CheckConstraint("ck_roll_kind_actioncatalogentry", "roll_kind IN ('AttackRoll', 'SavingThrow', 'None')");
                    table.CheckConstraint("ck_target_kind_actioncatalogentry", "target_kind IN ('Enemy', 'Ally', 'Point')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_action_catalog_entries_rules_system_key",
                table: "action_catalog_entries",
                columns: new[] { "rules_system", "key" },
                unique: true);

            // ── RLS: อ่านได้ทุกคน เขียนไม่ได้เลยแม้แต่ role แอป (เหตุผลเต็มดู doc comment บนคลาสนี้) ──
            migrationBuilder.Sql(@"
                ALTER TABLE action_catalog_entries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE action_catalog_entries FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_only ON action_catalog_entries FOR SELECT USING (true);
                REVOKE INSERT, UPDATE, DELETE ON action_catalog_entries FROM dndez_app;
            ");

            // ── Seed ชุดแรก — ค่ายืนยันแล้วโดย owner (SRD 5e/5.2), ดูที่มาใน
            // tests/DnDEz.Api.Tests/ActionCatalogSeedDataTests.cs ── รันด้วย role owner (dndez)
            // ตอน migrate จึงไม่ติด REVOKE ที่เพิ่งตั้งข้างบน
            migrationBuilder.Sql(@"
                INSERT INTO action_catalog_entries
                    (id, rules_system, key, name, kind, action_cost, spell_slot_level, target_kind,
                     range_normal_feet, range_long_feet, roll_kind, touches_ground)
                VALUES
                    (gen_random_uuid(), 'srd2024', 'longsword', 'Longsword', 'Weapon', 'Action', NULL, 'Enemy', 5, NULL, 'AttackRoll', NULL),
                    (gen_random_uuid(), 'srd2024', 'shortbow', 'Shortbow', 'Weapon', 'Action', NULL, 'Enemy', 80, 320, 'AttackRoll', NULL),
                    (gen_random_uuid(), 'srd2024', 'fire-bolt', 'Fire Bolt', 'Spell', 'Action', 0, 'Enemy', 120, NULL, 'AttackRoll', NULL),
                    (gen_random_uuid(), 'srd2024', 'magic-missile', 'Magic Missile', 'Spell', 'Action', 1, 'Enemy', 120, NULL, 'None', NULL),
                    (gen_random_uuid(), 'srd2024', 'walk', 'เดิน', 'Movement', 'None', NULL, 'Point', NULL, NULL, 'None', true),
                    (gen_random_uuid(), 'srd2024', 'jump', 'กระโดด', 'Movement', 'None', NULL, 'Point', NULL, NULL, 'None', false),
                    (gen_random_uuid(), 'srd2024', 'climb', 'ปีน', 'Movement', 'None', NULL, 'Point', NULL, NULL, 'None', false);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS read_only ON action_catalog_entries;
            ");

            migrationBuilder.DropTable(
                name: "action_catalog_entries");

            migrationBuilder.DropColumn(
                name: "charisma",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "constitution",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "dexterity",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "intelligence",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "speed_feet",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "strength",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "wisdom",
                table: "characters");
        }
    }
}

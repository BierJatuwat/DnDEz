using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint H Part 4 — เติมความเสียหาย (damage) + ความสามารถที่ใช้ทอย (ability) ให้ action_catalog_entries
    /// 7 แถวเดิม (Part 1 + Part 7) ที่เป็นอาวุธ/เวทโจมตีหรือรักษา
    ///
    /// เหตุผลที่เกินสเปก design doc §2.3 — §2.3 ระบุว่าคลังกลไกต้องมี range/target kind/cost/
    /// roll shape/type tags 5 อย่าง ไม่มี damage อยู่ในนั้น แต่ถ้าไม่มี damage_dice/ability ผู้เล่น
    /// ยังต้องพิมพ์ "1d20+5" / "1d8+3" เองอยู่ดี ทั้งที่ engine รู้ว่าใช้อาวุธ/เวทอะไรแล้ว — งานนี้ปิด
    /// ช่องว่างสุดท้ายระหว่าง "ระบบรู้กฎ" กับ "ผู้เล่นไม่ต้องคำนวณเอง" ยังไม่รวม upcast, คำอธิบาย
    /// เอฟเฟกต์พิเศษ (เช่น burning object ของ Fire Bolt) หรือ cantrip scaling — ของพวกนี้ยังอยู่นอกสโคป
    ///
    /// ค่าทุกตัวอ่านจาก SRD 5.2 ฉบับ CC (SRD_CC_v5.2.pdf) สองรอบแยกกัน — owner หนึ่งรอบ worker อีกรอบ
    /// ⚠️ ถ้าแปลง PDF เป็นข้อความ ต้องใช้ `pdftotext -raw` — `-layout` ทำคอลัมน์ Properties ของตาราง
    /// Weapons เลื่อนแถว (อ่าน Shortbow เป็น "Finesse" และ Greataxe เป็น "Ammunition" ซึ่งผิดทั้งคู่)
    ///  - ตาราง Weapons · Longsword: "1d8 Slashing ... Versatile (1d10)" — เก็บเป็น 1d8 มือเดียว ไม่ใช่
    ///    Versatile 1d10 เพราะ preset นักรบ (Part 7) ถือโล่คู่กับดาบ มือที่สองไม่ว่างจะถือสองมือ
    ///  - ตาราง Weapons · Shortbow: "1d6 Piercing Ammunition (Range 80/320; Arrow), Two-Handed"
    ///  - ตาราง Weapons · Greataxe: "1d12 Slashing Heavy, Two-Handed"
    ///  - Step 5: Character Creation Details (หัวข้อ Attacks): "Melee attack bonus = Strength modifier + Proficiency
    ///    Bonus" / "Ranged attack bonus = Dexterity modifier + Proficiency Bonus" และ "You add the same
    ///    ability modifier you use for attacks with a weapon to your damage rolls with that weapon."
    ///    ⇒ อาวุธ adds_ability_to_effect = true ทุกตัว
    ///  - Playing the Game › Damage and Healing (หัวข้อ Damage Rolls): "A spell tells you which dice to roll for damage and whether
    ///    to add any modifiers." ⇒ เวทบวก modifier เข้าผล **เฉพาะเมื่อตัวบทของเวทนั้นบอกไว้** ไม่ใช่กฎ
    ///    เหมารวมของ cantrip หรือของเวทโจมตี
    ///  - Fire Bolt: "Make a ranged spell attack ... the target takes 1d10 Fire damage." — ตัวบทไม่ได้
    ///    บอกให้บวก modifier ⇒ adds_ability_to_effect = false (spellcasting ability เข้าแค่ทอยโจมตี)
    ///  - Magic Missile: "A dart deals 1d4 + 1 Force damage" ยิงสามดอกพร้อมกัน — เก็บเป็น '3d4+3'
    ///    รวมทั้ง 3 ดอกเข้าเป้าหมายเดียวในแถวเดียว เพราะ 1 แถวของ affordance คือ 1 คู่ (action, target)
    ///    ยังไม่ได้โมเดลการกระจายดอกไปหลายเป้า · ability = NULL เพราะไม่ทอยโจมตี ไม่มี save และตัวบท
    ///    ไม่บวก modifier
    ///  - Sacred Flame: "a Dexterity saving throw or take 1d8 Radiant damage" — ability = Spellcasting
    ///    (คนทอย save คือเป้าหมาย แต่ DC มาจาก spellcasting ability ของผู้ร่าย: "Spell save DC = 8 +
    ///    your spellcasting ability modifier + your Proficiency Bonus") · ตัวบทไม่บวก modifier เข้า
    ///    ดาเมจ ⇒ adds_ability_to_effect = false
    ///  - Cure Wounds: "regains a number of Hit Points equal to 2d8 plus your spellcasting ability
    ///    modifier" — ใช้คอลัมน์ healing_dice แยกจาก damage_dice เพราะการรักษาไม่ใช่ชนิดดาเมจ ·
    ///    ตัวบทบวก modifier เอง ⇒ adds_ability_to_effect = true
    ///  - Finesse ไม่ถูกโมเดลในรอบนี้เพราะทั้ง 7 แถวไม่มีแถวไหนเป็นอาวุธ Finesse — นิยาม SRD:
    ///    "use your choice of your Strength or Dexterity modifier for the attack and damage rolls"
    ///    ยังไม่มีที่เก็บ ability สองทางเลือกในสคีมาปัจจุบัน ต้องเพิ่มเมื่อมีอาวุธ Finesse เข้าคลัง
    ///  - Cantrip damage scaling ที่ level 5/11/17 (Fire Bolt/Sacred Flame ทั้งคู่ระบุไว้ใน SRD) ไม่ถูก
    ///    โมเดล — damage_dice เก็บแค่ค่าที่ level ต่ำ ตัว Rules resolver คืนค่า "ไม่มีสูตร" ที่ level 5+
    ///    แทนการเดาสเกล
    ///
    /// ⚠️ **แก้บั๊ก id สุ่ม 4 แถว** — Part 1 seed ด้วย `gen_random_uuid()` แต่ Part 7 hardcode
    /// `actionCatalogRefs` ของ preset ให้ตรงกับ id ที่ owner เห็นบน dev DB ตอนนั้นเท่านั้น (ไม่ใช่ id
    /// ที่รับประกันว่าเหมือนกันทุกฐานข้อมูล) — owner migrate ฐานข้อมูลว่างชุดใหม่วันที่ 2026-09-11 แล้ว
    /// พบว่า ref ของ preset ค้าง (ไม่ชี้ไปแถวไหนเลย) 5 จาก 8 จุด (Fighter, Wizard ×2, Rogue, Ranger)
    /// ทำให้ตัวละครพวกนั้นได้ affordance ว่างเปล่า — migration นี้ pin id ของ longsword/shortbow/
    /// fire-bolt/magic-missile ให้ตรงกับค่าที่ Part 7 hardcode ไว้ ไม่กระทบ dev DB เดิม (เป็นการ
    /// UPDATE id ให้เป็นค่าเดิมที่มันเป็นอยู่แล้ว) แต่แก้ฐานข้อมูลใหม่ให้ถูกต้อง ตรวจ `pg_constraint`
    /// แล้วไม่มี foreign key อ้างมาที่ `action_catalog_entries` เลย จึงเปลี่ยน PK ตรงนี้ได้อย่างปลอดภัย
    /// (ผล psql จริงหลัง apply อยู่ใน `.claude/logs/routing.md`/รายงานของ dispatch นี้ ไม่ใช่ในคอมเมนต์
    /// นี้ ตามกติกาห้ามเขียน "ยืนยันแล้ว" ก่อนรันจริง)
    /// </summary>
    public partial class AddActionCatalogDamageAndAbility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ability",
                table: "action_catalog_entries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "adds_ability_to_effect",
                table: "action_catalog_entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "damage_dice",
                table: "action_catalog_entries",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "damage_type",
                table: "action_catalog_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "healing_dice",
                table: "action_catalog_entries",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ability_actioncatalogentry",
                table: "action_catalog_entries",
                sql: "ability IN ('Strength', 'Dexterity', 'Spellcasting')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_damage_pair_actioncatalogentry",
                table: "action_catalog_entries",
                sql: "(damage_dice IS NULL) = (damage_type IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_damage_type_actioncatalogentry",
                table: "action_catalog_entries",
                sql: "damage_type IN ('Acid', 'Bludgeoning', 'Cold', 'Fire', 'Force', 'Lightning', 'Necrotic', 'Piercing', 'Poison', 'Psychic', 'Radiant', 'Slashing', 'Thunder')");

            // ── เติม damage/ability ให้ 7 แถวเดิม (walk/jump/climb ไม่แตะ — ปล่อยเป็นดีฟอลต์คอลัมน์
            // NULL/false) ค่าตรวจกับ SRD 5.2 CC เอง ดูที่มาในคอมเมนต์บนคลาสนี้ ── รันด้วย role owner
            // (dndez) ตอน migrate จึงไม่ติด REVOKE ที่ Part 1 ตั้งไว้
            migrationBuilder.Sql(@"
                UPDATE action_catalog_entries SET damage_dice = '1d8', damage_type = 'Slashing', ability = 'Strength', adds_ability_to_effect = true WHERE rules_system = 'srd2024' AND key = 'longsword';
                UPDATE action_catalog_entries SET damage_dice = '1d6', damage_type = 'Piercing', ability = 'Dexterity', adds_ability_to_effect = true WHERE rules_system = 'srd2024' AND key = 'shortbow';
                UPDATE action_catalog_entries SET damage_dice = '1d12', damage_type = 'Slashing', ability = 'Strength', adds_ability_to_effect = true WHERE rules_system = 'srd2024' AND key = 'greataxe';
                UPDATE action_catalog_entries SET damage_dice = '1d10', damage_type = 'Fire', ability = 'Spellcasting', adds_ability_to_effect = false WHERE rules_system = 'srd2024' AND key = 'fire-bolt';
                UPDATE action_catalog_entries SET damage_dice = '3d4+3', damage_type = 'Force', ability = NULL, adds_ability_to_effect = false WHERE rules_system = 'srd2024' AND key = 'magic-missile';
                UPDATE action_catalog_entries SET damage_dice = '1d8', damage_type = 'Radiant', ability = 'Spellcasting', adds_ability_to_effect = false WHERE rules_system = 'srd2024' AND key = 'sacred-flame';
                UPDATE action_catalog_entries SET healing_dice = '2d8', ability = 'Spellcasting', adds_ability_to_effect = true WHERE rules_system = 'srd2024' AND key = 'cure-wounds';
            ");

            // ── แก้บั๊ก id สุ่ม: pin 4 แถวที่ preset (Part 7) hardcode actionCatalogRefs อ้างถึง ให้
            // ตรงทุกฐานข้อมูล ไม่ใช่แค่ dev DB ที่ owner ใช้ตอน seed Part 7 — เหตุผลเต็มในคอมเมนต์บน
            // คลาสนี้ ตรวจ pg_constraint แล้วไม่มี FK อ้างมาที่ action_catalog_entries จึงเปลี่ยน PK
            // ตรงนี้ได้อย่างปลอดภัย
            migrationBuilder.Sql(@"
                UPDATE action_catalog_entries SET id = '794fe6ea-b50c-4f74-b66b-eb2455c10301' WHERE rules_system = 'srd2024' AND key = 'longsword';
                UPDATE action_catalog_entries SET id = '8d52c9fc-8f3d-4405-bd71-f2bf09aa017b' WHERE rules_system = 'srd2024' AND key = 'shortbow';
                UPDATE action_catalog_entries SET id = '86636c2c-1c6b-4488-a5f5-f3ea56f984ba' WHERE rules_system = 'srd2024' AND key = 'fire-bolt';
                UPDATE action_catalog_entries SET id = '2d864a4f-130f-4375-9063-70863b3d4774' WHERE rules_system = 'srd2024' AND key = 'magic-missile';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // จงใจไม่ย้อน id ที่ pin ไว้ — id เดิมเป็นค่าสุ่มจาก gen_random_uuid() ไม่มีค่าที่มีความหมาย
            // ให้คืน และ id ที่ pin ไว้ไม่ทำอะไรเสียหาย (ข้อมูลดาเมจ/ability หายไปพร้อม DropColumn เอง)
            migrationBuilder.DropCheckConstraint(
                name: "ck_ability_actioncatalogentry",
                table: "action_catalog_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_damage_pair_actioncatalogentry",
                table: "action_catalog_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_damage_type_actioncatalogentry",
                table: "action_catalog_entries");

            migrationBuilder.DropColumn(
                name: "ability",
                table: "action_catalog_entries");

            migrationBuilder.DropColumn(
                name: "adds_ability_to_effect",
                table: "action_catalog_entries");

            migrationBuilder.DropColumn(
                name: "damage_dice",
                table: "action_catalog_entries");

            migrationBuilder.DropColumn(
                name: "damage_type",
                table: "action_catalog_entries");

            migrationBuilder.DropColumn(
                name: "healing_dice",
                table: "action_catalog_entries");
        }
    }
}

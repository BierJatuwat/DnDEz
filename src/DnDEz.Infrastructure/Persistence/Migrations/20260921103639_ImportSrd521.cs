using System;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// นำเข้า System Reference Document 5.2.1 (CC-BY-4.0) เข้าระบบ: มอนสเตอร์ 330 ตัว · เวท 339 ตัว ·
    /// condition 15 ตัว + กฎที่มีป้ายกำกับอีก 26 รายการ (Action / Area of Effect / Hazard / Attitude)
    ///
    /// ## ที่มาของข้อมูล — แยกจาก PDF ด้วยสคริปต์ ไม่ได้พิมพ์มือ
    /// ต้นฉบับ `docs/SRD_CC_v5.2.1.pdf` → `tools/srd-import/build_srd_data.py` (pdftotext -enc UTF-8 -raw)
    /// → `Seed/Srd/srd-5.2.1-*.json` (ฝังเป็น embedded resource) · สคริปต์ **พังทันที** ถ้าผลไม่ตรงตัวเลขอ้างอิงในต้นฉบับ:
    ///  - รายชื่อมอนสเตอร์ต้องตรงกับ "Index of Stat Blocks" ท้ายเล่ม 330/330
    ///  - เวททุกชื่อในรายชื่อเวทของแต่ละคลาสต้องอยู่ในเวทที่แยกได้ (รวม 339)
    ///  - condition ต้องครบ 15 ตามรายการในบท Rules Glossary
    ///  - มอนสเตอร์ทุกตัว: HP เฉลี่ยต้องตรงกับสูตรเต๋า และโบนัสเต๋า = จำนวนลูกเต๋า × CON modifier
    /// ตัวเลขข้างบนคือผลที่วัดตอนสร้างไฟล์ข้อมูล ไม่ใช่ตัวเลขจากความจำ
    ///
    /// ## สิ่งที่ *ไม่* ได้นำเข้า (ตั้งใจ — เขียนไว้ไม่ให้เงียบ)
    ///  - **กฎในบท Rules Glossary ที่ไม่มีป้ายกำกับ** (~110 รายการ เช่น Advantage, Cover, Opportunity Attacks): ข้อความดิบจาก PDF
    ///    ไม่บอกว่าบรรทัดไหนเป็นหัวข้อ จึงแยกได้แค่แบบเดาซึ่งยังไม่แม่นพอจะเก็บเป็นกฎอ้างอิง — เฉพาะรายการที่มี "[ป้าย]" เท่านั้น
    ///  - คลาส/สปีชีส์/ฟีท/อาวุธ/เกราะ/ไอเทมเวทมนตร์ — ยังไม่มีตารางรองรับ (action_catalog_entries ที่ใช้คำนวณจริงยังเป็น 10 แถวเดิม)
    ///  - ตารางในเวท/กฎ: ข้อความครบแต่โครงแถว/คอลัมน์หาย (ข้อจำกัดของการอ่านข้อความดิบจาก PDF)
    ///  - stat block ของเวทเรียกอัญเชิญ (Otherworldly Steed ฯลฯ): AC/HP ขึ้นกับระดับช่องเวท จึงเป็นส่วนของข้อความเวท
    /// พิมพ์ผิดในต้นฉบับที่แก้ให้ (พิสูจน์ได้จากสูตร): Young White Dragon — save ของ INT ตกเครื่องหมายลบ (บันทึกเป็น
    /// `sourceFixup` ใน data ของมอนสเตอร์) · Adult White Dragon — "Con22" ไม่มีช่องว่าง
    ///
    /// ## สิทธิ์: อ่านอย่างเดียวสำหรับ role แอป
    /// `srd_monsters` เป็นตารางใหม่แต่ **ไม่ผูกแคมเปญ** จึงไม่ใช้ policy แบบ campaign_isolation — ใช้ RLS แบบเดียวกับ
    /// `action_catalog_entries` (ENABLE + FORCE + policy read_only + REVOKE เขียน) · เพิ่ม REVOKE/RLS ให้ `rules_corpus_entries`
    /// ด้วย เพราะตารางนี้เพิ่งเริ่มมีข้อมูลจริงและก่อนหน้านี้ role แอปเขียนทับได้ (ค่า DML ถูก grant โดย DEFAULT PRIVILEGES)
    ///
    /// ## การแก้ข้อมูลทีหลัง
    /// migration ที่ apply แล้วห้ามแก้ · ถ้าต้องแก้ข้อมูล ให้สร้างไฟล์ `srd-5.2.1-*-v2.json` + migration ใหม่ที่ upsert
    /// (SQL ด้านล่างเป็น ON CONFLICT DO UPDATE อยู่แล้ว รันซ้ำได้)
    ///
    /// ⚠️ ต้องใส่ข้อความ attribution ของ CC-BY-4.0 ในสิ่งที่แสดงต่อผู้ใช้ (ดู `SrdAttribution.Text` ใน DnDEz.Api)
    /// </summary>
    public partial class ImportSrd521 : Migration
    {
        private const string Version = "5.2.1";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "data",
                table: "rules_corpus_entries",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.CreateTable(
                name: "srd_monsters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    size = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    alignment = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    armor_class = table.Column<int>(type: "integer", nullable: false),
                    hit_points = table.Column<int>(type: "integer", nullable: false),
                    speed_feet = table.Column<int>(type: "integer", nullable: false),
                    reach_feet = table.Column<int>(type: "integer", nullable: false),
                    challenge_rating = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    xp = table.Column<int>(type: "integer", nullable: false),
                    source_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_srd_monsters", x => x.id);
                    table.CheckConstraint("ck_srd_monster_numbers", "armor_class > 0 AND hit_points > 0 AND speed_feet >= 0 AND reach_feet >= 5");
                });

            migrationBuilder.CreateIndex(
                name: "ix_srd_monsters_key",
                table: "srd_monsters",
                column: "key",
                unique: true);

            // ── seed ก่อนปิดสิทธิ์เขียน (migration รันด้วย role owner อยู่แล้ว ไม่ติด REVOKE — แต่เรียงลำดับนี้ให้อ่านง่าย) ──
            migrationBuilder.Sql($@"
                INSERT INTO srd_monsters
                    (id, key, name, size, type, alignment, armor_class, hit_points, speed_feet, reach_feet,
                     challenge_rating, xp, source_version, data)
                SELECT gen_random_uuid(), e->>'key', e->>'name', e->>'size', e->>'type', e->>'alignment',
                       (e->>'armorClass')::int, (e->>'hitPoints')::int, (e->>'speedFeet')::int, (e->>'reachFeet')::int,
                       e->>'cr', (e->>'xp')::int, '{Version}', e
                FROM jsonb_array_elements({Payload("monsters")}::jsonb) AS e
                ON CONFLICT (key) DO UPDATE SET
                    name = EXCLUDED.name, size = EXCLUDED.size, type = EXCLUDED.type, alignment = EXCLUDED.alignment,
                    armor_class = EXCLUDED.armor_class, hit_points = EXCLUDED.hit_points,
                    speed_feet = EXCLUDED.speed_feet, reach_feet = EXCLUDED.reach_feet,
                    challenge_rating = EXCLUDED.challenge_rating, xp = EXCLUDED.xp,
                    source_version = EXCLUDED.source_version, data = EXCLUDED.data;
            ");

            // เวท: content = คำอธิบาย + ส่วน "Using a Higher-Level Spell Slot"/"Cantrip Upgrade" · โครงสร้างอยู่ใน data
            migrationBuilder.Sql($@"
                INSERT INTO rules_corpus_entries (id, category, key, title, content, license, source, data)
                SELECT gen_random_uuid(), 'spell', e->>'key', e->>'name',
                       CASE WHEN e->>'higherLevel' IS NULL THEN e->>'description'
                            ELSE (e->>'description') || E'\n\n' || (e->>'higherLevel') END,
                       'CC-BY-4.0', 'SRD {Version}',
                       (e - 'description' - 'higherLevel')
                FROM jsonb_array_elements({Payload("spells")}::jsonb) AS e
                ON CONFLICT (category, key) DO UPDATE SET
                    title = EXCLUDED.title, content = EXCLUDED.content, source = EXCLUDED.source, data = EXCLUDED.data;
            ");

            // กฎที่มีป้ายกำกับ: ป้าย Condition → category 'condition' (ตรงกับที่ผู้เล่นกดดู tooltip) · ที่เหลือ → 'rule'
            migrationBuilder.Sql($@"
                INSERT INTO rules_corpus_entries (id, category, key, title, content, license, source, data)
                SELECT gen_random_uuid(),
                       CASE WHEN e->>'tag' = 'Condition' THEN 'condition' ELSE 'rule' END,
                       e->>'key', e->>'title', e->>'text',
                       'CC-BY-4.0', 'SRD {Version}', jsonb_build_object('tag', e->>'tag')
                FROM jsonb_array_elements({Payload("rules")}::jsonb) AS e
                ON CONFLICT (category, key) DO UPDATE SET
                    title = EXCLUDED.title, content = EXCLUDED.content, source = EXCLUDED.source, data = EXCLUDED.data;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE srd_monsters ENABLE ROW LEVEL SECURITY;
                ALTER TABLE srd_monsters FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_only ON srd_monsters FOR SELECT USING (true);
                REVOKE INSERT, UPDATE, DELETE ON srd_monsters FROM dndez_app;

                ALTER TABLE rules_corpus_entries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE rules_corpus_entries FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_only ON rules_corpus_entries FOR SELECT USING (true);
                REVOKE INSERT, UPDATE, DELETE ON rules_corpus_entries FROM dndez_app;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                DROP POLICY IF EXISTS read_only ON rules_corpus_entries;
                ALTER TABLE rules_corpus_entries NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE rules_corpus_entries DISABLE ROW LEVEL SECURITY;
                GRANT INSERT, UPDATE, DELETE ON rules_corpus_entries TO dndez_app;
                DELETE FROM rules_corpus_entries WHERE source = 'SRD {Version}';
            ");

            migrationBuilder.DropTable(
                name: "srd_monsters");

            migrationBuilder.DropColumn(
                name: "data",
                table: "rules_corpus_entries");
        }

        /// <summary>
        /// อ่านไฟล์ข้อมูลที่ฝังไว้แล้วห่อเป็น dollar-quoted literal (ไม่ต้อง escape เครื่องหมายคำพูดใน JSON)
        /// tag `$srd$` ต้องไม่ปรากฏในข้อมูลเอง — ตรวจก่อนใช้ทุกครั้งเพื่อกัน SQL พังแบบเงียบ
        /// </summary>
        private static string Payload(string kind)
        {
            var resource = $"Seed.Srd.srd-{Version}-{kind}.json";
            using var stream = typeof(ImportSrd521).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"ไม่พบ embedded resource {resource}");
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
            var json = reader.ReadToEnd();
            if (json.Contains("$srd$", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{resource} มี '$srd$' อยู่ในข้อมูล — เปลี่ยน tag ของ dollar-quote");
            }

            return $"$srd${json}$srd$";
        }
    }
}

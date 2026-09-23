using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// คำแปลภาษาไทยของคลัง SRD 5.2.1 — ปุ่มสลับภาษาที่ผู้ใช้ขอ (2026-09-22)
    ///
    /// ## ที่มาของข้อมูล — ทีมพัฒนาแปลเอง ไม่ใช่บริการแปลอัตโนมัติ
    /// `tools/srd-translate/*.py` เก็บคำแปลเป็น dict ในซอร์สโค้ด → `build_th_data.py` ตรวจกับต้นฉบับ
    /// อังกฤษแล้วเขียนไฟล์ JSON ที่ฝังไว้ (เหมือน `tools/srd-import` กับ `ImportSrd521`) — สคริปต์**พังทันที**
    /// ถ้าคำแปลไม่ครบ 100% ตามที่กำหนด หรือ key ไม่ตรงกับต้นฉบับ
    ///
    /// ⚠️ **นี่คือคำแปลเพื่อความเข้าใจ ไม่ใช่คำแปลทางการที่ผ่านการรับรอง** — ดู doc comment เต็มใน
    /// `tools/srd-translate/th_glossary.py`
    ///
    /// ## ขอบเขตของรอบนี้ (ตั้งใจ ไม่ใช่ลืม)
    ///  - **ชื่อมอนสเตอร์/ชื่อเวท/ชื่อ condition-rule ไม่แปล** — ผู้ใช้ยืนยัน (2026-09-22 ผ่าน
    ///    AskUserQuestion) ว่าชื่อไอเทม/ชื่อมอนสเตอร์/"ชื่อเฉพาะ" (รวมชื่อเวทกับชื่อ condition/rule)
    ///    ให้คงเป็นอังกฤษเสมอ แปลเฉพาะบริบทโดยรอบ — คอลัมน์ `name`/`title` ด้านล่างจึงเก็บ**ค่าอังกฤษ**
    ///    ไม่ใช่คำแปล (ยังต้องมีแถวเพราะ column เป็น NOT NULL แต่ค่าเท่าต้นฉบับเสมอ ไม่ใช่ "แปลแล้วบังเอิญ
    ///    เหมือนกัน") — ดู `tools/srd-translate/build_th_data.py`
    ///  - ขนาด/ชนิด/แนวคิด/สำนักเวท/คลาส (ศัพท์ปิด ~46 คำ ใช้ซ้ำได้กับทุกแถวแทบไม่มีต้นทุนเพิ่ม) แปลครบทุกแถว
    ///  - **สถานะ 15 + กฎมีป้ายกำกับ 26 (รวม 41/41) แปลเนื้อหาเต็ม** (เฉพาะ `text`/เนื้อหา ไม่ใช่ `title`)
    ///    — ใช้เป็น tooltip บ่อยที่สุด
    ///  - **รายละเอียดเต็มของมอนสเตอร์/เวท (traits/actions/คำอธิบาย) แปลแล้วเฉพาะ "ชุดที่ใช้จริงก่อน"**:
    ///    มอนสเตอร์ด่านแรก 8 ตัว (ก็อบลินนักรบ/หัวหน้า, หมาป่า, โครงกระดูก, ซอมบี้, หนูยักษ์, แมงมุมยักษ์,
    ///    สเตอร์จ) — ตัวที่เหลือ API คืนอังกฤษพร้อมธง `contentTranslated: false`
    ///  - **เวท 84/339 ตัว**: คาถริป (ระดับ 0) ครบ 27/27 + เวทระดับ 1 ครบ 57/57 (2026-09-22 — ผู้ใช้เลือก
    ///    "เวทระดับต่ำก่อน" แล้วสั่ง "แปลต่อ" ดู `tools/srd-translate/starter_full_th.py`) · เวทระดับ 2
    ///    ขึ้นไปที่เหลือ (255 ตัว) ยังไม่มี `content`/`data` แปล — เพิ่มได้ทีหลังผ่าน migration ใหม่ที่
    ///    upsert (SQL ด้านล่างเป็น ON CONFLICT DO UPDATE อยู่แล้ว)
    ///
    /// ## สิทธิ์: อ่านอย่างเดียวสำหรับ role แอป — เหมือน `ImportSrd521` ทุกประการ
    /// ตารางใหม่ทั้งสองไม่ผูกแคมเปญ (global) เนื้อหามาจาก migration เท่านั้น ไม่มี endpoint ให้เขียน
    /// </summary>
    public partial class AddSrdThaiTranslations : Migration
    {
        private const string Locale = "th";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rules_corpus_translations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules_corpus_translations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "srd_monster_translations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    monster_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    size = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    type_tag = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    alignment = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_srd_monster_translations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rules_corpus_translations_category_key_locale",
                table: "rules_corpus_translations",
                columns: new[] { "category", "key", "locale" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_srd_monster_translations_monster_key_locale",
                table: "srd_monster_translations",
                columns: new[] { "monster_key", "locale" },
                unique: true);

            // ── seed (migration รันด้วย role owner จึงไม่ติด REVOKE ที่ตั้งไว้ท้ายไฟล์นี้) ──
            migrationBuilder.Sql($@"
                INSERT INTO srd_monster_translations (id, monster_key, locale, name, size, type, type_tag, alignment, data)
                SELECT gen_random_uuid(), e->>'key', '{Locale}', e->>'name', e->>'size', e->>'type', e->>'typeTag',
                       e->>'alignment', e->'data'
                FROM jsonb_array_elements({Payload("monsters-th")}::jsonb) AS e
                ON CONFLICT (monster_key, locale) DO UPDATE SET
                    name = EXCLUDED.name, size = EXCLUDED.size, type = EXCLUDED.type,
                    type_tag = EXCLUDED.type_tag, alignment = EXCLUDED.alignment, data = EXCLUDED.data;
            ");

            // เวท: name/school/classes เก็บใน data · description/higherLevel (ถ้ามี) รวมเป็น content เดียว
            // แบบเดียวกับที่ ImportSrd521 ทำกับ rules_corpus_entries.content ของอังกฤษ
            migrationBuilder.Sql($@"
                INSERT INTO rules_corpus_translations (id, category, key, locale, title, content, data)
                SELECT gen_random_uuid(), 'spell', e->>'key', '{Locale}', e->>'name',
                       CASE WHEN e ? 'description'
                            THEN (CASE WHEN e->>'higherLevel' IS NULL THEN e->>'description'
                                       ELSE (e->>'description') || E'\n\n' || (e->>'higherLevel') END)
                            ELSE NULL END,
                       jsonb_build_object('school', e->>'school', 'classes', e->'classes')
                FROM jsonb_array_elements({Payload("spells-th")}::jsonb) AS e
                ON CONFLICT (category, key, locale) DO UPDATE SET
                    title = EXCLUDED.title, content = EXCLUDED.content, data = EXCLUDED.data;
            ");

            // กฎ/สถานะ: category ตามป้าย เหมือน ImportSrd521 ทุกประการ (Condition -> 'condition' ที่เหลือ -> 'rule')
            migrationBuilder.Sql($@"
                INSERT INTO rules_corpus_translations (id, category, key, locale, title, content, data)
                SELECT gen_random_uuid(),
                       CASE WHEN e->>'tag' = 'Condition' THEN 'condition' ELSE 'rule' END,
                       e->>'key', '{Locale}', e->>'title', e->>'text', NULL
                FROM jsonb_array_elements({Payload("rules-th")}::jsonb) AS e
                ON CONFLICT (category, key, locale) DO UPDATE SET
                    title = EXCLUDED.title, content = EXCLUDED.content;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE srd_monster_translations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE srd_monster_translations FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_only ON srd_monster_translations FOR SELECT USING (true);
                REVOKE INSERT, UPDATE, DELETE ON srd_monster_translations FROM dndez_app;

                ALTER TABLE rules_corpus_translations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE rules_corpus_translations FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_only ON rules_corpus_translations FOR SELECT USING (true);
                REVOKE INSERT, UPDATE, DELETE ON rules_corpus_translations FROM dndez_app;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rules_corpus_translations");

            migrationBuilder.DropTable(
                name: "srd_monster_translations");
        }

        /// <summary>อ่านไฟล์คำแปลที่ฝังไว้แล้วห่อเป็น dollar-quoted literal — เหมือน `ImportSrd521.Payload` เป๊ะ</summary>
        private static string Payload(string kind)
        {
            var resource = $"Seed.Srd.srd-5.2.1-{kind}.json";
            using var stream = typeof(AddSrdThaiTranslations).Assembly.GetManifestResourceStream(resource)
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

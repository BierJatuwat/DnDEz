using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint G Part 7 — คลัง preset ตัวละคร + seed ชุดแรก (design doc §5, player-side-design §18-19)
    ///
    /// ไม่มี schema เปลี่ยนเลย — `character_presets` มีอยู่แล้วตรงกับสเปกแบบ 1:1 (ดู doc comment ของ
    /// `CharacterPreset` entity) งานนี้จึงเป็น "เขียนข้อมูล" ล้วน ๆ ผ่าน migration (ตาม convention
    /// เดิมของโปรเจกต์ที่ seed ผ่าน migration เท่านั้น ไม่มี endpoint/mechanism อื่นให้ seed ข้อมูลระบบ)
    ///
    /// ⚠️ **เพิ่ม action_catalog_entries ใหม่ 3 ตัว** (Greataxe, Sacred Flame, Cure Wounds) นอกเหนือ
    /// จาก 7 ตัวที่ Part 1 seed ไว้ — คลังเดิมมีแค่ดาบยาว/ธนูสั้น/Fire Bolt/Magic Missile ซึ่งไม่พอ
    /// ครอบคลาสพื้นฐาน 6 คลาสที่ Part นี้ต้องการ (ไม่มี weapon ให้บาร์บาเรียน ไม่มีเวทให้คนบวช) —
    /// เพิ่มแบบเดียวกับที่ Part 1 ทำ (owner ยืนยันค่า SRD เองก่อน ไม่ dispatch ให้ haiku) ค่าที่ยืนยัน:
    ///  - **Greataxe**: อาวุธ martial melee heavy two-handed มาตรฐาน ระยะประชิด 5 ฟุต ทอย vs AC
    ///  - **Sacred Flame**: cantrip โจมตีด้วย **Dexterity saving throw** (ไม่ใช่ attack roll — เป็น
    ///    entry แรกที่ roll_kind=SavingThrow ในคลัง) ระยะ 60 ฟุต
    ///  - **Cure Wounds**: เวทระดับ 1 ระยะสัมผัส (touch — แทนด้วย range_normal_feet=5 แบบเดียวกับ
    ///    melee เพราะเป้าหมายต้องอยู่ติดกัน) auto-succeed ไม่มีทอย (roll_kind=None แบบเดียวกับ Magic
    ///    Missile) target_kind=**Ally** (entry แรกที่ target ฝ่ายเดียวกัน ก่อนหน้านี้มีแต่ Enemy/Point)
    ///
    /// ⚠️ **preset ทั้ง 6 คำนวณจาก standard array (15,14,13,12,10,8) + กฎ 5e มาตรฐาน** ที่ owner
    /// ยืนยันเองด้วยความมั่นใจสูง (กฎเสถียร ไม่เปลี่ยนข้ามฉบับ): level 1 HP = hit die สูงสุด + CON mod
    /// (ไม่ใช่ทอย/เฉลี่ย — กฎ 5e บังคับใช้ค่าสูงสุดที่ level 1 เสมอ), AC จากชุดเกราะมาตรฐานของแต่ละคลาส
    /// รายละเอียดการคำนวณอยู่ในคอมเมนต์ทีละ preset ด้านล่าง
    ///
    /// ⚠️ **`level_path` เป็น scaffold ระดับ 1 เท่านั้นโดยตั้งใจ** — ไม่ได้ทำ auto-level เต็มรูป
    /// (progression ของแต่ละคลาสที่ level 2+ ต้องตรวจ SRD ทีละคลาส ไม่ใช่ของ Part นี้) เก็บแค่
    /// `{"1": {"maxHp": N}}` เป็นโครงให้ Part ในอนาคตขยายต่อได้ ไม่ใช่ progression ที่ใช้งานได้จริงเลย
    ///
    /// ⚠️ **ไม่มี catalog ref สำหรับเกราะ/โล่เลย** — armor ไม่ใช่ "สิ่งที่ engine ต้องคำนวณ affordance"
    /// (design doc §2.3) เป็นแค่ตัวเลขคงที่ที่ผสมเข้า ArmorClass ของตัวละครตรง ๆ ต่างจากอาวุธ/เวทที่
    /// เป็น action ที่ต้องอ้างอิงคลังกลไก (`actionCatalogRefs`) — equipment string ที่เป็นเกราะ/โล่จึง
    /// เป็นแค่ flavor text ไม่มี ref ตามหลัง ไม่ใช่ omission
    /// </summary>
    public partial class SeedActionCatalogAdditionsAndCharacterPresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO action_catalog_entries
                    (id, rules_system, key, name, kind, action_cost, spell_slot_level, target_kind,
                     range_normal_feet, range_long_feet, roll_kind, save_ability)
                VALUES
                    ('10000000-0000-0000-0000-000000000001', 'srd2024', 'greataxe', 'Greataxe', 'Weapon', 'Action', NULL, 'Enemy', 5, NULL, 'AttackRoll', NULL),
                    ('10000000-0000-0000-0000-000000000002', 'srd2024', 'sacred-flame', 'Sacred Flame', 'Spell', 'Action', 0, 'Enemy', 60, NULL, 'SavingThrow', 'Dexterity'),
                    ('10000000-0000-0000-0000-000000000003', 'srd2024', 'cure-wounds', 'Cure Wounds', 'Spell', 'Action', 1, 'Ally', 5, NULL, 'None', NULL);
                """);

            // ── นักรบ (Fighter) — STR 15/DEX 13/CON 14/INT 12/WIS 10/CHA 8
            //    HP: d10 (max ที่ level 1) + CON mod(+2) = 12
            //    AC: เกราะโซ่ Chain Mail (หนัก, AC 16 คงที่ไม่บวก dex) + โล่ Shield (+2) = 18
            //    อาวุธ: ดาบยาว Longsword (อ้างอิง catalog Part 1)
            migrationBuilder.Sql("""
                INSERT INTO character_presets
                    (id, scope, campaign_id, source, name, build, level_path, level,
                     build_fingerprint, popularity_count, srd_only)
                VALUES (
                    '20000000-0000-0000-0000-000000000001', 'Global', NULL, 'Authored', 'นักรบ (Fighter)',
                    '{"race":"Human","class":"Fighter","abilityScores":{"strength":15,"dexterity":13,"constitution":14,"intelligence":12,"wisdom":10,"charisma":8},"speedFeet":30,"armorClass":18,"hitPoints":12,"equipment":["เกราะโซ่ (Chain Mail)","โล่ (Shield)","ดาบยาว (Longsword)"],"actionCatalogRefs":["794fe6ea-b50c-4f74-b66b-eb2455c10301"]}'::jsonb,
                    '{"1":{"maxHp":12}}'::jsonb, 1, 'authored-fighter-v1', 1, true
                );
                """);

            // ── จอมเวทย์ (Wizard) — INT 15/DEX 14/CON 13/WIS 12/CHA 10/STR 8
            //    HP: d6 (max) + CON mod(+1) = 7 · AC: ไม่มีเกราะ (ไม่ได้ฝึกใส่) = 10 + DEX mod(+2) = 12
            //    เวท: Fire Bolt (cantrip) + Magic Missile (ระดับ 1) — อ้างอิง catalog Part 1
            migrationBuilder.Sql("""
                INSERT INTO character_presets
                    (id, scope, campaign_id, source, name, build, level_path, level,
                     build_fingerprint, popularity_count, srd_only)
                VALUES (
                    '20000000-0000-0000-0000-000000000002', 'Global', NULL, 'Authored', 'จอมเวทย์ (Wizard)',
                    '{"race":"Human","class":"Wizard","abilityScores":{"strength":8,"dexterity":14,"constitution":13,"intelligence":15,"wisdom":12,"charisma":10},"speedFeet":30,"armorClass":12,"hitPoints":7,"equipment":["คัมภีร์เวทมนตร์ (Spellbook)","ถุงส่วนประกอบเวท (Component Pouch)"],"actionCatalogRefs":["86636c2c-1c6b-4488-a5f5-f3ea56f984ba","2d864a4f-130f-4375-9063-70863b3d4774"]}'::jsonb,
                    '{"1":{"maxHp":7}}'::jsonb, 1, 'authored-wizard-v1', 1, true
                );
                """);

            // ── จอมโจร (Rogue) — DEX 15/CON 14/INT 13/WIS 12/CHA 10/STR 8
            //    HP: d8 (max) + CON mod(+2) = 10 · AC: เกราะหนัง Leather (เบา, AC 11 + dex เต็ม) + DEX mod(+2) = 13
            //    อาวุธ: ธนูสั้น Shortbow (โจรฝึกอาวุธ simple ได้ตามกฎ SRD — อ้างอิง catalog Part 1)
            migrationBuilder.Sql("""
                INSERT INTO character_presets
                    (id, scope, campaign_id, source, name, build, level_path, level,
                     build_fingerprint, popularity_count, srd_only)
                VALUES (
                    '20000000-0000-0000-0000-000000000003', 'Global', NULL, 'Authored', 'จอมโจร (Rogue)',
                    '{"race":"Human","class":"Rogue","abilityScores":{"strength":8,"dexterity":15,"constitution":14,"intelligence":13,"wisdom":12,"charisma":10},"speedFeet":30,"armorClass":13,"hitPoints":10,"equipment":["เกราะหนัง (Leather Armor)","ธนูสั้น (Shortbow)","ชุดเครื่องมือโจร (Thieves Tools)"],"actionCatalogRefs":["8d52c9fc-8f3d-4405-bd71-f2bf09aa017b"]}'::jsonb,
                    '{"1":{"maxHp":10}}'::jsonb, 1, 'authored-rogue-v1', 1, true
                );
                """);

            // ── นักบวช (Cleric) — WIS 15/CON 14/DEX 13/STR 12/CHA 10/INT 8
            //    HP: d8 (max) + CON mod(+2) = 10
            //    AC: เกราะเกล็ด Scale Mail (กลาง, AC 14 + dex สูงสุด 2) + DEX mod(+1, ไม่ชน cap) + โล่(+2) = 17
            //    เวท: Sacred Flame (cantrip) + Cure Wounds (ระดับ 1) — entry ใหม่ที่เพิ่งเพิ่มด้านบน
            migrationBuilder.Sql("""
                INSERT INTO character_presets
                    (id, scope, campaign_id, source, name, build, level_path, level,
                     build_fingerprint, popularity_count, srd_only)
                VALUES (
                    '20000000-0000-0000-0000-000000000004', 'Global', NULL, 'Authored', 'นักบวช (Cleric)',
                    '{"race":"Human","class":"Cleric","abilityScores":{"strength":12,"dexterity":13,"constitution":14,"intelligence":8,"wisdom":15,"charisma":10},"speedFeet":30,"armorClass":17,"hitPoints":10,"equipment":["เกราะเกล็ด (Scale Mail)","โล่ (Shield)","สัญลักษณ์ศักดิ์สิทธิ์ (Holy Symbol)"],"actionCatalogRefs":["10000000-0000-0000-0000-000000000002","10000000-0000-0000-0000-000000000003"]}'::jsonb,
                    '{"1":{"maxHp":10}}'::jsonb, 1, 'authored-cleric-v1', 1, true
                );
                """);

            // ── เรนเจอร์ (Ranger) — DEX 15/WIS 14/CON 13/STR 12/INT 10/CHA 8
            //    HP: d10 (max) + CON mod(+1) = 11 · AC: เกราะหนังศึกษา Studded Leather (เบา, AC 12 + dex เต็ม) + DEX mod(+2) = 14
            //    อาวุธ: ธนูสั้น Shortbow (อาวุธประจำตัวเรนเจอร์คลาสสิก — อ้างอิง catalog Part 1)
            migrationBuilder.Sql("""
                INSERT INTO character_presets
                    (id, scope, campaign_id, source, name, build, level_path, level,
                     build_fingerprint, popularity_count, srd_only)
                VALUES (
                    '20000000-0000-0000-0000-000000000005', 'Global', NULL, 'Authored', 'เรนเจอร์ (Ranger)',
                    '{"race":"Human","class":"Ranger","abilityScores":{"strength":12,"dexterity":15,"constitution":13,"intelligence":10,"wisdom":14,"charisma":8},"speedFeet":30,"armorClass":14,"hitPoints":11,"equipment":["เกราะหนังศึกษา (Studded Leather)","ธนูสั้น (Shortbow)"],"actionCatalogRefs":["8d52c9fc-8f3d-4405-bd71-f2bf09aa017b"]}'::jsonb,
                    '{"1":{"maxHp":11}}'::jsonb, 1, 'authored-ranger-v1', 1, true
                );
                """);

            // ── บาร์บาเรียน (Barbarian) — STR 15/CON 14/DEX 13/WIS 12/CHA 10/INT 8
            //    HP: d12 (max, hit die สูงสุดในเกม) + CON mod(+2) = 14
            //    AC: Unarmored Defense (feature เฉพาะคลาส ไม่ใส่เกราะ) = 10 + DEX mod(+1) + CON mod(+2) = 13
            //    อาวุธ: ขวานใหญ่สองมือ Greataxe (entry ใหม่ที่เพิ่งเพิ่มด้านบน)
            migrationBuilder.Sql("""
                INSERT INTO character_presets
                    (id, scope, campaign_id, source, name, build, level_path, level,
                     build_fingerprint, popularity_count, srd_only)
                VALUES (
                    '20000000-0000-0000-0000-000000000006', 'Global', NULL, 'Authored', 'บาร์บาเรียน (Barbarian)',
                    '{"race":"Human","class":"Barbarian","abilityScores":{"strength":15,"dexterity":13,"constitution":14,"intelligence":8,"wisdom":12,"charisma":10},"speedFeet":30,"armorClass":13,"hitPoints":14,"equipment":["ขวานใหญ่สองมือ (Greataxe)"],"actionCatalogRefs":["10000000-0000-0000-0000-000000000001"]}'::jsonb,
                    '{"1":{"maxHp":14}}'::jsonb, 1, 'authored-barbarian-v1', 1, true
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM character_presets WHERE id IN (
                    '20000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000002',
                    '20000000-0000-0000-0000-000000000003', '20000000-0000-0000-0000-000000000004',
                    '20000000-0000-0000-0000-000000000005', '20000000-0000-0000-0000-000000000006'
                );
                DELETE FROM action_catalog_entries WHERE id IN (
                    '10000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000002',
                    '10000000-0000-0000-0000-000000000003'
                );
                """);
        }
    }
}

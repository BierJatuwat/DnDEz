using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// แก้ช่องโหว่ทางสถาปัตยกรรมที่พบตอนออกแบบ Sprint A Part 3 (CRUD): policy เดิมของ `campaigns`
    /// (`id = app_current_campaign() AND app_is_member()`) ผูกกับ `app.campaign_id` ตัวเดียวเสมอ
    /// เพราะ `app_current_campaign()` คืนค่าได้แค่ค่าเดียวต่อ request — แปลว่า query แบบไม่มี WHERE
    /// (`SELECT * FROM campaigns`) กรองได้ **แถวเดียวเป็นอย่างมาก** ไม่ว่าผู้ใช้จะเป็นสมาชิกกี่แคมเปญ
    /// ก็ตาม → **"list แคมเปญของตัวเอง" เป็นไปไม่ได้เลยในทางโครงสร้าง** ไม่ใช่แค่ต้องเจาะจง id ก่อน
    /// (ตารางลูกอื่น เช่น sessions/characters ไม่มีปัญหานี้ เพราะ use case ของมันคือ "อยู่ในแคมเปญ
    /// เดียวที่เลือกไว้แล้ว" เสมอ ต่างจาก campaigns ที่ต้อง list ข้ามหลายแคมเปญได้)
    ///
    /// แก้ด้วยฟังก์ชันใหม่ `app_is_member_of(uuid)` (SECURITY DEFINER แบบเดียวกับ `app_is_member()`
    /// เดิม — กัน policy ของ campaign_members วนเรียกตัวเอง) ที่เช็คสมาชิกภาพต่อ **campaign_id ที่รับ
    /// เป็น parameter** (ค่าของแต่ละแถวที่กำลังถูกประเมิน) แทนที่จะผูกกับ `app_current_campaign()`
    /// ตัวเดียว — ใช้เป็น policy ใหม่ของ `campaigns` สำหรับ SELECT เท่านั้น
    ///
    /// INSERT/UPDATE/DELETE ของ campaigns ไม่มี policy ใด ๆ ครอบหลัง migration นี้ (เดิมมีร่วมกับ
    /// SELECT ใน policy เดียว) — ภายใต้ FORCE ROW LEVEL SECURITY แปลว่า **role dndez_app ทำ
    /// INSERT/UPDATE/DELETE ตรงบนตาราง campaigns ไม่ได้เลยอีกต่อไป** (fail closed ตามปกติของระบบนี้)
    /// ซึ่งตรงกับเจตนา: การสร้างแคมเปญต้องผ่าน `app_create_campaign()` เท่านั้น (SECURITY DEFINER
    /// bypass policy นี้ไปเลยเพราะรันด้วยสิทธิ์ owner) ส่วน UPDATE/DELETE ยังไม่มี feature ในเฟสนี้
    /// เพิ่ม policy ทีหลังได้เมื่อมี use case จริง (เช่น "DM แก้ชื่อแคมเปญ")
    /// </summary>
    public partial class FixCampaignListRlsPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS campaign_isolation ON campaigns;

                -- SECURITY DEFINER เพื่อไม่ให้ policy ของ campaign_members วนเรียกตัวเอง
                -- (หลักการเดียวกับ app_is_member() เดิม ต่างกันแค่รับ campaign_id เป็น parameter
                -- แทนการผูกกับ app_current_campaign() ตัวเดียว — ทำให้เช็คได้ทีละแถวตอน list หลายแคมเปญ)
                CREATE OR REPLACE FUNCTION app_is_member_of(p_campaign_id uuid) RETURNS boolean
                    LANGUAGE sql STABLE SECURITY DEFINER AS
                $$
                    SELECT EXISTS (
                        SELECT 1 FROM campaign_members m
                        WHERE m.campaign_id = p_campaign_id
                          AND m.user_id     = app_current_user_id()
                    )
                $$;

                CREATE POLICY campaign_member_read ON campaigns
                    FOR SELECT
                    USING (app_is_member_of(id));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS campaign_member_read ON campaigns;
                DROP FUNCTION IF EXISTS app_is_member_of(uuid);

                CREATE POLICY campaign_isolation ON campaigns
                    USING (id = app_current_campaign() AND app_is_member())
                    WITH CHECK (id = app_current_campaign() AND app_is_member());
                """);
        }
    }
}

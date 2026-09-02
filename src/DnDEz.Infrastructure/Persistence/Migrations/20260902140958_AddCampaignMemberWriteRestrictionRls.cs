using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint F งานที่ 3 — ปิดช่องโหว่ privilege escalation บน `campaign_members` (finding "H2" จาก
    /// security review รอบ Sprint F งานที่ 2a) ตัดสิน 2026-09-02
    ///
    ///  1. **ช่องโหว่คืออะไร** — `campaign_members` เป็น **ตารางที่นิยามว่าใครเป็น DM** แต่มี policy
    ///     เดียวคือ `campaign_isolation` (`FOR ALL`, `app_is_member()`) ที่ตกทอดมาจาก loop
    ///     "ทุกตารางที่มี campaign_id" ใน migration `RowLevelSecurity` (20260821094727) →
    ///     สมาชิกธรรมดาคนไหนก็ `UPDATE campaign_members SET role = 'Dm'` แถวตัวเองได้
    ///
    ///  2. **ทำไมร้ายแรงกว่าการแก้ข้อมูลจุดเดียว** — `app_is_dm()` อ่านจากตารางนี้ตรง ๆ การเลื่อนขั้น
    ///     ตัวเองที่นี่จึง **ปลดล็อกทุก policy ที่พึ่ง `app_is_dm()` พร้อมกัน** (`autonomy_settings`
    ///     `dm_write`, `source_documents`/`raw_chunks` `dm_only`) — เป็นจุด pivot ไม่ใช่ปลายทาง
    ///
    ///     ยืนยันจริงก่อนแก้ (รันกับ Postgres ผ่าน role `dndez_app` 2026-09-02): `app_is_dm()` = false
    ///     → `UPDATE campaign_members SET role='Dm'` คืน **UPDATE 1** → `app_is_dm()` = true →
    ///     INSERT ลง `autonomy_settings` ที่เป็น DM-only **สำเร็จ** (rollback ทิ้งหลังทดลอง)
    ///     red test ที่ล็อกไว้: `tests/DnDEz.Api.Tests/CampaignMemberRoleRlsTests.cs` (ก่อนแก้ fail 5
    ///     ผ่าน 5)
    ///
    ///  3. ⚠️ **ยังไม่ reachable ผ่าน HTTP ตอนที่แก้** — ไม่มีโค้ด C# เขียนตารางนี้เลยสักที่ (ที่เดียว
    ///     ที่แตะคือ **อ่าน** ที่ `CampaignMembershipEndpoints.cs:136`) เขียนได้เฉพาะผ่าน
    ///     `app_create_campaign()` / `app_join_campaign()` ซึ่ง hardcode role ทั้งคู่ · ปิดตอนนี้เพื่อ
    ///     ไม่ให้ฟีเจอร์ในอนาคต (เช่น endpoint "เตะสมาชิก") กลายเป็นตัวเปิดช่องโหว่โดยไม่มีใครสังเกต
    ///
    ///  4. ⚠️⚠️ **ทำไมไม่ลอก `member_read` + `dm_write` ของ `autonomy_settings` มาทั้งดุ้น** —
    ///     สองตารางนี้ต่างกันตรงที่ **ใครเขียนจริง**:
    ///       • `autonomy_settings` — DM เขียนผ่าน HTTP จริง (PUT autonomy-settings) → ต้องมี `dm_write`
    ///       • `campaign_members` — **ไม่มีใครเขียนผ่าน HTTP เลย** → ไม่ต้องมี write policy เลยสักตัว
    ///     หลักที่ยึดคือ "สิทธิ์ที่ให้ = สิทธิ์ที่แอปใช้จริง" ไม่ใช่การลอกรูปทรงของ policy ก่อนหน้า
    ///
    ///     ถ้าลอก `dm_write` มาด้วยจะเปิดให้ DM `DELETE` แถว `role='Dm'` ของตัวเองได้ → พัง invariant
    ///     "เจ้าของแคมเปญเป็นสมาชิก role Dm เสมอ" ซึ่งเป็น**ตัวเชื่อมนิยาม DM สองชั้นเข้าหากัน**
    ///     (ชั้น C# = `campaigns.owner_user_id`, ชั้น DB = `campaign_members.role`) ถ้า invariant นั้นขาด
    ///     สองชั้นจะไม่ตรงกันเงียบ ๆ: C# ยังคิดว่าเป็น DM แต่ RLS ไม่ให้เขียน `autonomy_settings` แล้ว
    ///
    ///  5. **ทำไม `app_join_campaign()` / `app_create_campaign()` ยัง INSERT ได้ทั้งที่ไม่มี write policy**
    ///     — ตรวจจริงแล้ว (`pg_roles`, `pg_class` 2026-09-02) ไม่ใช่เดา:
    ///       • ทั้งสองฟังก์ชันเป็น `SECURITY DEFINER` ที่ **owner คือ role `dndez`**
    ///       • `dndez` เป็น **superuser + BYPASSRLS** → ข้าม RLS ทั้งหมดตอนรันในฟังก์ชัน
    ///
    ///     ⚠️ ที่รอด **ไม่ใช่เพราะ "SECURITY DEFINER ข้าม RLS"** — มันไม่ข้ามด้วยตัวมันเอง มันแค่
    ///     เปลี่ยน current_user เป็น owner เท่านั้น และ `campaign_members` ตั้ง **FORCE ROW LEVEL
    ///     SECURITY** ไว้ (`relforcerowsecurity = true`) ซึ่งปกติทำให้แม้แต่เจ้าของตารางก็ยังโดน policy
    ///     บังคับ · สิ่งเดียวที่พาให้รอดคือ attribute superuser/BYPASSRLS ของ role `dndez`
    ///     **ถ้าวันหลังย้าย owner ของฟังก์ชันไป role ธรรมดา (ซึ่งเป็นสิ่งที่ควรทำในเชิง least privilege)
    ///     FORCE RLS จะมีผลทันทีและ INSERT ในฟังก์ชันจะพัง** — ตอนนั้นต้องเพิ่ม policy ให้ role นั้น
    ///     โดยเจาะจง · เทสต์ `App_join_campaign_still_inserts_membership_after_policy_change` คือตัวที่
    ///     จะจับเคสนี้ให้
    ///
    ///  6. **ไม่มีปัญหา recursion** ที่ policy บน `campaign_members` เรียก `app_is_member()` ซึ่งเอง
    ///     ก็ SELECT `campaign_members` — เพราะฟังก์ชันเป็น SECURITY DEFINER ที่ owner ข้าม RLS
    ///     (ข้อ 5) query ข้างในจึงไม่ถูก policy คลุมซ้ำ · ของเดิม (`campaign_isolation`) ก็เรียก
    ///     `app_is_member()` แบบนี้อยู่แล้วและใช้งานได้จริงมาตลอด — ไม่ใช่พฤติกรรมใหม่ที่เพิ่งพึ่ง
    /// </summary>
    public partial class AddCampaignMemberWriteRestrictionRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // เจตนา: เหลือเฉพาะ SELECT ให้สมาชิก · INSERT/UPDATE/DELETE ไม่มี policy ไหนคลุมเลย
            // → ทุกคำสั่งเขียนจาก role `dndez_app` ถูกปฏิเสธ (UPDATE/DELETE ได้ 0 rows เพราะ USING
            // ซ่อนแถว, INSERT ได้ error 42501 เพราะไม่มี WITH CHECK ไหนยอมรับ)
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS campaign_isolation ON campaign_members;

                CREATE POLICY member_read ON campaign_members
                    FOR SELECT
                    USING (campaign_id = app_current_campaign() AND app_is_member());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS member_read ON campaign_members;

                CREATE POLICY campaign_isolation ON campaign_members
                    USING (campaign_id = app_current_campaign() AND app_is_member())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_member());
                """);
        }
    }
}

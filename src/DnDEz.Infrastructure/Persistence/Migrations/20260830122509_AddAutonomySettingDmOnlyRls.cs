using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// แก้ RLS policy ของ `autonomy_settings` ให้แยก SELECT ออกจาก write แทนที่ policy รวมตัวเดียว
    /// (ตัดสิน 2026-08-30) เหตุผล:
    ///
    ///  1. **ทำไมต้องแก้** — `autonomy_settings` คือ dial มอบอำนาจของ DM (AI ทำอะไรเองได้บ้างโดยไม่
    ///     ต้องขออนุมัติ ต่อ subsystem) ไม่ใช่ log ที่สมาชิกเขียนร่วมกันแบบ `dice_rolls`/`messages` แต่
    ///     migration `RowLevelSecurity` (20260821094727) กวาดมันเข้า loop "ทุกตารางที่มี campaign_id"
    ///     จนได้ policy รวม `campaign_isolation` (`app_is_member()`, FOR ALL) → สมาชิกคนไหนก็หมุน
    ///     dial ได้ในสายตา DB
    ///
    ///  2. **ตาม precedent เดิม** — migration เดียวกันนั้นแยกแยะไว้ถูกแล้วกับ `source_documents`/
    ///     `raw_chunks` ที่ได้ policy `dm_only` บน `app_is_dm()` — `autonomy_settings` แค่ตกหล่นไป
    ///
    ///  3. ⚠️ **ทำไมสอง policy ไม่ใช่ `dm_only FOR ALL` ตัวเดียว** — `FOR ALL` เอา `USING` ไปคุม
    ///     `SELECT` ด้วย ถ้าใช้ตัวเดียวจะพัง `GET /campaigns/{id}/autonomy-settings` ที่จงใจเปิดให้
    ///     สมาชิกทุกคนอ่าน (ผู้เล่นควรรู้ว่า AI มีอำนาจแค่ไหนในโต๊ะที่ตัวเองเล่น) · RLS policy ของ
    ///     Postgres เป็น PERMISSIVE โดยดีฟอลต์และ OR กันภายใน command เดียวกัน การแยกจึงได้ผล:
    ///     SELECT ของผู้เล่นผ่านทาง `member_read` ตัวเดียวพอ ส่วน INSERT/UPDATE/DELETE ไม่มี policy
    ///     ไหนเป็นตัวเลือกเลยนอกจาก `dm_write` (policy ที่เป็น FOR SELECT ไม่มีผลกับคำสั่งเขียน) →
    ///     `app_is_dm()` เป็น false → โดนบล็อก
    ///
    ///  4. ⚠️ **สองชั้นนิยาม "DM" ไม่ตรงกันเป๊ะ** — `app_is_dm()` ดูจาก
    ///     `campaign_members.role = 'Dm'` ส่วนเช็คใน C# (`AutonomySettingEndpoints`) ดูจาก
    ///     `campaigns.owner_user_id` · ที่ตรงกันได้ทุกวันนี้เพราะ `app_create_campaign()` insert
    ///     เจ้าของเป็นสมาชิก role `'Dm'` เสมอ — invariant นี้คือสิ่งที่ทำให้สองชั้นเข้ากัน · เช็คใน C#
    ///     ยังอยู่เหมือนเดิม RLS เป็นชั้นที่สองอิสระ (defense in depth) ไม่ใช่ตัวแทน
    /// </summary>
    public partial class AddAutonomySettingDmOnlyRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS campaign_isolation ON autonomy_settings;

                CREATE POLICY member_read ON autonomy_settings
                    FOR SELECT
                    USING (campaign_id = app_current_campaign() AND app_is_member());

                CREATE POLICY dm_write ON autonomy_settings
                    FOR ALL
                    USING (campaign_id = app_current_campaign() AND app_is_dm())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_dm());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS dm_write ON autonomy_settings;
                DROP POLICY IF EXISTS member_read ON autonomy_settings;

                CREATE POLICY campaign_isolation ON autonomy_settings
                    USING (campaign_id = app_current_campaign() AND app_is_member())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_member());
                """);
        }
    }
}

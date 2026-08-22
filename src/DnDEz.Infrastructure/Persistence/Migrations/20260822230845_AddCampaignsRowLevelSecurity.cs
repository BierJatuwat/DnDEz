using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// แก้ช่องโหว่: migration RowLevelSecurity เดิม (20260821094727) หา RLS ให้เฉพาะตารางที่มีคอลัมน์
    /// campaign_id ทำให้ตาราง campaigns เอง (ตารางแม่ ใช้ id เป็น PK ไม่มีคอลัมน์ campaign_id) หลุดออกจาก
    /// loop ไปโดยไม่มีใครตั้งใจ — role dndez_app ที่ได้ GRANT ALL TABLES ไปแล้วจึงอ่าน/เขียนทุกแถวใน
    /// campaigns ได้หมดโดยไม่มีการเช็คสิทธิ์ใด ๆ เลย (fail-open) migration นี้ปิดช่องโหว่โดยเทียบ id
    /// ของตัวเองกับ app_current_campaign() แทน
    /// </summary>
    /// <inheritdoc />
    public partial class AddCampaignsRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE campaigns ENABLE ROW LEVEL SECURITY;
                ALTER TABLE campaigns FORCE ROW LEVEL SECURITY;
                CREATE POLICY campaign_isolation ON campaigns
                    USING (id = app_current_campaign() AND app_is_member())
                    WITH CHECK (id = app_current_campaign() AND app_is_member());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS campaign_isolation ON campaigns;
                ALTER TABLE campaigns DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}

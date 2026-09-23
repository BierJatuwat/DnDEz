using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ปิด `consult_messages` ให้เป็น DM-only ที่ชั้น RLS (2026-09-22, Part C ของแผน "DM AI Assistant")
    ///
    ///  1. **ทำไมต้องแก้ตอนนี้** — ตารางนี้ถูกสร้างไว้ตั้งแต่ `InitialSchema` แต่**ไม่เคยมี endpoint
    ///     ไหนใช้เลย** จนถึงวันนี้ที่ `AdvisorEndpoints` เริ่มเขียนลงจริง · และสิ่งที่จะเขียนลงคือ
    ///     **บทสนทนาที่ AI เห็น `StoryBeat.DmNotes`/`Secrets` ของ DM** (ต่างจาก `/consult` ที่จงใจกัน
    ///     ข้อมูลสองอย่างนี้ออกจาก prompt เพราะ path นั้นใช้ร่วมกับ `/actions` ที่ broadcast — ดู doc
    ///     comment ของ `OrchestratorTurnRunner`) คำตอบของ AI จึงอาจอ้างถึงความลับของแคมเปญตรง ๆ
    ///
    ///  2. **ของเดิมเปิดให้สมาชิกทุกคนอ่านได้** — migration `RowLevelSecurity` (20260821094727) กวาด
    ///     ตารางนี้เข้า loop "ทุกตารางที่มี campaign_id" จนได้ policy รวม `campaign_isolation`
    ///     (`app_is_member()`, FOR ALL) เหมือนที่เคยเกิดกับ `autonomy_settings` แล้วต้องมาแก้ใน
    ///     `AddAutonomySettingDmOnlyRls` (20260830122509) — เคสนี้คือ pattern เดียวกันซ้ำอีกรอบ
    ///
    ///  3. ⚠️ **ที่นี่ใช้ `dm_only FOR ALL` ตัวเดียวถูกต้องแล้ว ต่างจาก `autonomy_settings`** —
    ///     migration นั้นต้องแยกเป็น `member_read` + `dm_write` เพราะผู้เล่น **ควร** อ่าน dial ได้
    ///     (`GET /campaigns/{id}/autonomy-settings` เปิดให้ทุกคน) แต่แชทที่ปรึกษาส่วนตัวของ DM ผู้เล่น
    ///     **ไม่ควรอ่านได้เลยแม้แต่แถวเดียว** การที่ `FOR ALL` เอา `USING` ไปคุม SELECT ด้วยจึงเป็น
    ///     ผลลัพธ์ที่ต้องการพอดี ไม่ใช่ผลข้างเคียงที่ต้องเลี่ยง (เทียบกับบทเรียน Sprint F งานที่ 2a ที่
    ///     `dm_only FOR ALL` เคยเป็นบั๊กเพราะไปบล็อก SELECT ที่ตั้งใจเปิด — เจตนาคนละแบบกัน)
    ///     · ยืนยันด้วยเทสต์จริงใน `AdvisorEndpointsTests` ไม่ใช่เชื่อเหตุผลบนกระดาษ
    ///
    ///  4. RLS เป็นชั้นที่สองอิสระ — `AdvisorEndpoints` ยังเช็ค `RequireCampaignDmAsync`
    ///     (`campaigns.owner_user_id`) ที่ชั้น C# เหมือนเดิม ส่วน `app_is_dm()` ดูจาก
    ///     `campaign_members.role = 'Dm'` · สองนิยามเข้ากันได้เพราะ `app_create_campaign()` insert
    ///     เจ้าของเป็นสมาชิก role `'Dm'` เสมอ (invariant เดียวกับที่ `AddAutonomySettingDmOnlyRls` อธิบายไว้)
    /// </summary>
    public partial class AddConsultMessageDmOnlyRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS campaign_isolation ON consult_messages;

                CREATE POLICY dm_only ON consult_messages
                    FOR ALL
                    USING (campaign_id = app_current_campaign() AND app_is_dm())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_dm());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS dm_only ON consult_messages;

                CREATE POLICY campaign_isolation ON consult_messages
                    USING (campaign_id = app_current_campaign() AND app_is_member())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_member());
                """);
        }
    }
}

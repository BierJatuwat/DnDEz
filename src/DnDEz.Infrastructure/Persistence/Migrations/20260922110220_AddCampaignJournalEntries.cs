using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// "บันทึกแคมเปญ" (Part D ของแผน "DM AI Assistant", 2026-09-22) — ชั้นรายละเอียดที่อยู่ระหว่าง log
    /// ดิบของเซสชันกับ `recap_snapshots` ที่สรุปสั้น · DM เขียนเองก็ได้ หรือให้ AI สรุปให้ก็ได้
    /// (คอลัมน์ `authored_by` บอกที่มา — ผู้ใช้ตัดสินให้เปิดกว้างทั้งสองทาง ไม่บังคับทางใดทางหนึ่ง)
    ///
    /// ## ⚠️ RLS: เขียนเองทั้งหมดในไฟล์นี้ ไม่มี auto-inherit
    /// `dotnet ef migrations add` สร้างให้แค่ `CreateTable` + FK + CHECK เท่านั้น — **ไม่มี RLS ติดมาเลย**
    /// และ `ALTER DEFAULT PRIVILEGES` เดิมยัง grant DML เต็มให้ role `dndez_app` อยู่ → ตารางใหม่
    /// **fail OPEN ไม่ใช่ fail closed** ถ้าไม่เขียนบล็อกด้านล่างเอง (กฎถาวรใน CLAUDE.md หัวข้อ "ตารางใหม่
    /// ทุกตัวต้องเปิด RLS เอง" — บล็อก `DO $$ ... FOR t IN SELECT ... $$` ใน migration `RowLevelSecurity`
    /// เป็น one-time script ที่รันครั้งเดียวตอนนั้น ไม่ใช่ trigger ถาวร)
    ///
    /// ## ทำไม `dm_only FOR ALL` ตัวเดียว ไม่ใช่ `member_read` + `dm_write` แบบ `autonomy_settings`
    /// เนื้อหาบันทึกอาจมีความลับของแคมเปญปนอยู่เสมอ — โดยเฉพาะรายการที่ AI สรุปให้ ซึ่ง AI เห็น
    /// `StoryBeat.DmNotes`/`Secrets` ผ่าน `/advisor` (ดู `AdvisorEndpoints`) ผู้เล่นจึงต้องอ่านไม่ได้
    /// **แม้แต่แถวเดียว** การที่ `FOR ALL` เอา `USING` ไปคุม SELECT ด้วยจึงเป็นผลที่ต้องการพอดี — เหตุผล
    /// เดียวกับ `AddConsultMessageDmOnlyRls` (2026-09-22) ไม่ใช่เคสของ `AddAutonomySettingDmOnlyRls`
    /// ที่ผู้เล่น *ควร* อ่าน dial ได้จึงต้องแยกสอง policy
    ///
    /// วันไหนที่อยากให้ผู้เล่นอ่านบันทึกบางรายการได้ ให้เพิ่มคอลัมน์ `visibility` แล้วค่อยแยกเป็น 2 policy
    /// **ห้ามผ่อน policy ตัวนี้เฉย ๆ** เพราะจะเปิดทั้งตารางรวมรายการที่มีความลับไปด้วย
    /// </summary>
    public partial class AddCampaignJournalEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaign_journal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    authored_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_journal_entries", x => x.id);
                    table.CheckConstraint("ck_authored_by_campaignjournalentry", "authored_by IN ('Dm', 'Ai')");
                    table.ForeignKey(
                        name: "fk_campaign_journal_entries_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_campaign_journal_entries_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_journal_entries_campaign_id_created_at",
                table: "campaign_journal_entries",
                columns: new[] { "campaign_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_journal_entries_session_id",
                table: "campaign_journal_entries",
                column: "session_id");

            // ── RLS: เขียนเองทั้งหมด (ดู doc comment ของคลาสว่าทำไมขาดไม่ได้) ──
            migrationBuilder.Sql("""
                ALTER TABLE campaign_journal_entries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE campaign_journal_entries FORCE ROW LEVEL SECURITY;

                CREATE POLICY dm_only ON campaign_journal_entries
                    FOR ALL
                    USING (campaign_id = app_current_campaign() AND app_is_dm())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_dm());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_journal_entries");
        }
    }
}

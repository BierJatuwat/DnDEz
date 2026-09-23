using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 2026-09-23 — ผู้ช่วย DM ช่องเดียว (แทน `/advisor` + `/consult`)
    ///
    /// - `assistant_runs` เก็บงานที่หยุดรอ DM อนุมัติ พร้อมบทสนทนาดิบและ system prompt ที่มี
    ///   `StoryBeat.DmNotes`/`Secrets` → **ENABLE + FORCE RLS + `dm_only` FOR ALL** ในไฟล์นี้เอง (ตารางใหม่
    ///   fail open ถ้าลืม — ดูกฎถาวรใน CLAUDE.md) · FOR ALL ตัวเดียวถูกต้องที่นี่เพราะผู้เล่นต้องอ่านไม่ได้
    ///   แม้แต่แถวเดียว (ต่างจาก `autonomy_settings` ที่ต้องแยก member_read)
    /// - `consult_messages` เพิ่ม `session_id` + `activity` — ตารางนี้ `dm_only` อยู่แล้วจาก
    ///   `AddConsultMessageDmOnlyRls` ไม่ต้องแตะ policy
    /// </summary>
    public partial class AddAssistantRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "activity",
                table: "consult_messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "consult_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assistant_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dm_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_runs", x => x.id);
                    table.CheckConstraint("ck_status_assistantrun", "status IN ('Running', 'AwaitingApproval', 'Completed', 'Abandoned')");
                    table.ForeignKey(
                        name: "fk_assistant_runs_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assistant_runs_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_consult_messages_session_id",
                table: "consult_messages",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_assistant_runs_campaign_id_dm_user_id_status",
                table: "assistant_runs",
                columns: new[] { "campaign_id", "dm_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_runs_session_id",
                table: "assistant_runs",
                column: "session_id");

            migrationBuilder.Sql("""
                ALTER TABLE assistant_runs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE assistant_runs FORCE ROW LEVEL SECURITY;

                CREATE POLICY dm_only ON assistant_runs
                    FOR ALL
                    USING (campaign_id = app_current_campaign() AND app_is_dm())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_dm());
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_consult_messages_sessions_session_id",
                table: "consult_messages",
                column: "session_id",
                principalTable: "sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_consult_messages_sessions_session_id",
                table: "consult_messages");

            migrationBuilder.DropTable(
                name: "assistant_runs");

            migrationBuilder.DropIndex(
                name: "ix_consult_messages_session_id",
                table: "consult_messages");

            migrationBuilder.DropColumn(
                name: "activity",
                table: "consult_messages");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "consult_messages");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint F งานที่ 4 (ชั้น DB) — ตาราง `encounters` + `encounter_participants` สำหรับระบบ
    /// initiative/combat พร้อมคอลัมน์ `characters.initiative_modifier`
    ///
    ///  1. ⚠️ **กับดักที่ต้องบันทึกไว้ — ตารางใหม่ "fail open" ไม่ใช่ "fail closed"**
    ///     ใน migration `RowLevelSecurity` (20260821094727) มี `DO $$` block ที่วน
    ///     `FOR t IN SELECT ... WHERE column_name = 'campaign_id' ... CREATE POLICY` เพื่อติด
    ///     `campaign_isolation` ให้ทุกตารางที่มีคอลัมน์ `campaign_id`
    ///     **แต่นั่นคือโค้ดที่รันครั้งเดียวตอน migrate นั้น ไม่ใช่ event trigger** — ตารางที่สร้าง
    ///     ทีหลัง (อย่าง `encounters` ที่มี `campaign_id` เต็ม ๆ) **ไม่ได้ policy อัตโนมัติ**
    ///
    ///     ที่อันตรายคือ `ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE,
    ///     DELETE ON TABLES TO dndez_app` ใน migration เดียวกัน **ยังมีผลกับตารางใหม่อยู่** ⇒
    ///     ตารางใหม่ได้ GRANT ครบทันทีแต่ไม่มี RLS = **role ของแอปอ่าน/เขียนข้ามแคมเปญได้ทั้งหมด**
    ///     การลืมเขียน policy จึงเปิดช่องโหว่เงียบ ๆ ไม่ใช่ทำให้พัง (fail open) — ตารางใหม่ทุกตัวที่มี
    ///     ข้อมูลผูกแคมเปญ **ต้องเขียน ENABLE/FORCE RLS + CREATE POLICY ด้วยมือใน migration เสมอ**
    ///
    ///     ยืนยันจริงหลัง apply (2026-09-02): `pg_class` → ทั้งสองตาราง `relrowsecurity` = t และ
    ///     `relforcerowsecurity` = t · `pg_policies` → `campaign_isolation` cmd `ALL` ตารางละ 1 policy ·
    ///     `role_table_grants` → `dndez_app` ได้ SELECT/INSERT/UPDATE/DELETE ครบทั้งสองตารางจาก
    ///     ALTER DEFAULT PRIVILEGES โดยไม่ต้อง GRANT เพิ่มในไฟล์นี้ (ตรงกับที่อธิบายไว้ข้างบน)
    ///
    ///  2. **ทำไม `encounter_participants` ต้องใช้ EXISTS-chain** — ตารางนี้ไม่มี `campaign_id`
    ///     ตรง ๆ (ผูกแคมเปญผ่าน `encounter_id` → `encounters.campaign_id`) จึงใช้ไม่ได้กับ policy
    ///     รูปแบบ `campaign_id = app_current_campaign()` · ลอกรูปทรงจากตารางลูกของ `sessions`
    ///     (`messages`, `scenes`, `dice_rolls` ฯลฯ) ใน `20260821094727_RowLevelSecurity.cs` ที่ใช้
    ///     `EXISTS (SELECT 1 FROM sessions s WHERE s.id = %I.session_id AND s.campaign_id =
    ///     app_current_campaign()) AND app_is_member()` — เปลี่ยนตารางแม่จาก `sessions` เป็น
    ///     `encounters` เท่านั้น · `AND app_is_member()` ขาดไม่ได้ ด้วยเหตุผลเดียวกับที่ระบุไว้ใน
    ///     `app_is_member()`: ถ้าเทียบแค่ `campaign_id` กับค่าที่ session setting ตั้งมา แอปที่บั๊ก
    ///     หรือถูกเจาะแล้วตั้ง `app.campaign_id` เป็นของแคมเปญอื่นก็อ่านได้ทันที
    ///
    ///  3. **`encounter_participants.character_id` ใช้ `ON DELETE CASCADE` ไม่ใช่ `RESTRICT`** —
    ///     "multiple cascade paths" ที่เคยเป็นเหตุผลให้เลือก RESTRICT เป็นข้อจำกัดของ SQL Server
    ///     ไม่ใช่ Postgres · Postgres รองรับหลาย cascade path มาถึงตารางเดียวกันได้ปกติ ในขณะที่
    ///     RESTRICT สร้างปัญหาจริง: `DELETE FROM campaigns` cascade ลงทั้ง `characters` และ
    ///     `encounters` พร้อมกัน ถ้า RI trigger ฝั่ง `characters` ทำงานก่อนที่ `encounter_participants`
    ///     จะถูกลบตาม `encounters` จะได้ FK violation แบบ non-deterministic — ยืนยันด้วย cleanup
    ///     ของ `EncounterRlsTests` ที่ลบแคมเปญทิ้งจริงแล้วผ่าน
    /// </summary>
    public partial class AddEncounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "initiative_modifier",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "encounter_participants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    encounter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initiative_roll = table.Column<int>(type: "integer", nullable: false),
                    initiative_modifier = table.Column<int>(type: "integer", nullable: false),
                    initiative_total = table.Column<int>(type: "integer", nullable: false),
                    turn_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_encounter_participants", x => x.id);
                    table.CheckConstraint("ck_encounter_participants_initiative_roll", "initiative_roll BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_encounter_participants_initiative_total", "initiative_total = initiative_roll + initiative_modifier");
                    table.CheckConstraint("ck_encounter_participants_turn_order", "turn_order >= 0");
                    table.ForeignKey(
                        name: "fk_encounter_participants_characters_character_id",
                        column: x => x.character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "encounters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_participant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_encounters", x => x.id);
                    table.ForeignKey(
                        name: "fk_encounters_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_encounters_encounter_participants_current_participant_id",
                        column: x => x.current_participant_id,
                        principalTable: "encounter_participants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_encounters_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_characters_initiative_modifier",
                table: "characters",
                sql: "initiative_modifier BETWEEN -20 AND 20");

            migrationBuilder.CreateIndex(
                name: "ix_encounter_participants_character_id",
                table: "encounter_participants",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "ix_encounter_participants_encounter_id_character_id",
                table: "encounter_participants",
                columns: new[] { "encounter_id", "character_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_encounter_participants_encounter_id_turn_order",
                table: "encounter_participants",
                columns: new[] { "encounter_id", "turn_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_encounters_campaign_id",
                table: "encounters",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_encounters_current_participant_id",
                table: "encounters",
                column: "current_participant_id");

            migrationBuilder.CreateIndex(
                name: "ix_encounters_session_id_ended_at",
                table: "encounters",
                columns: new[] { "session_id", "ended_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_encounter_participants_encounters_encounter_id",
                table: "encounter_participants",
                column: "encounter_id",
                principalTable: "encounters",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // ── RLS: ต้องเขียนด้วยมือ · ดูเหตุผลข้อ 1 และ 2 ใน doc comment ของคลาสนี้ ──────────
            migrationBuilder.Sql(@"
                ALTER TABLE encounters ENABLE ROW LEVEL SECURITY;
                ALTER TABLE encounters FORCE ROW LEVEL SECURITY;
                CREATE POLICY campaign_isolation ON encounters
                    USING (campaign_id = app_current_campaign() AND app_is_member())
                    WITH CHECK (campaign_id = app_current_campaign() AND app_is_member());

                ALTER TABLE encounter_participants ENABLE ROW LEVEL SECURITY;
                ALTER TABLE encounter_participants FORCE ROW LEVEL SECURITY;
                CREATE POLICY campaign_isolation ON encounter_participants
                    USING (EXISTS (SELECT 1 FROM encounters e
                        WHERE e.id = encounter_participants.encounter_id
                          AND e.campaign_id = app_current_campaign()) AND app_is_member())
                    WITH CHECK (EXISTS (SELECT 1 FROM encounters e
                        WHERE e.id = encounter_participants.encounter_id
                          AND e.campaign_id = app_current_campaign()) AND app_is_member());
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS campaign_isolation ON encounter_participants;
                DROP POLICY IF EXISTS campaign_isolation ON encounters;
            ");

            migrationBuilder.DropForeignKey(
                name: "fk_encounter_participants_encounters_encounter_id",
                table: "encounter_participants");

            migrationBuilder.DropTable(
                name: "encounters");

            migrationBuilder.DropTable(
                name: "encounter_participants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_characters_initiative_modifier",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "initiative_modifier",
                table: "characters");
        }
    }
}

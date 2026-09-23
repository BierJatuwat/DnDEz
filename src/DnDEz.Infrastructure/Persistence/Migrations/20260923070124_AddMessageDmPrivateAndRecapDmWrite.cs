using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 2026-09-23 — สองเรื่องที่ต้องมาคู่กันก่อนจะให้ AI สรุป session เป็น recap ได้อย่างปลอดภัย
    ///
    /// ## 1. `messages.dm_private` + RLS ซ่อนแถวลับจากผู้เล่น
    /// `/consult` (DM ปรึกษา AI ส่วนตัว ไม่ broadcast) กับ `/actions` (เทิร์นผู้เล่น broadcast ทั้งโต๊ะ)
    /// เขียนลง `messages` ตารางเดียวกันโดยไม่มีอะไรบอกว่าแถวไหนเป็นของใคร — และ history ของเทิร์นผู้เล่น
    /// ดึง log ทั้ง session ไปให้ Claude รวมถึงที่ DM ปรึกษาลับ ๆ ด้วย จึงรั่วเข้า narration ที่ broadcast ได้
    ///
    /// policy ใหม่: สมาชิกอ่าน/เขียนได้เฉพาะแถว `dm_private = false` · DM ได้ทุกแถว — ผู้เล่นจึงสร้างแถวที่
    /// อ้างว่าเป็นของลับ DM ไม่ได้ และอ่านของลับไม่ได้แม้เช็คใน C# จะหายไป · ⚠️ ชั้น DB กันได้แค่ตาม
    /// "ตัวตน" — DM ที่เล่นผ่าน `/actions` ด้วยตัวละครของตัวเอง (broadcast) ยังอ่านแถวลับได้ ตัวที่กันเคสนั้น
    /// คือ filter ใน `OrchestratorTurnRunner.BuildHistoryAsync` ที่ผูกกับการ broadcast ไม่ใช่ตัวตน
    ///
    /// backfill แถวเดิม (ไม่มีข้อมูลว่ามาจาก path ไหน จึงอนุมานแบบ fail closed): ข้อความ DM → ลับ ·
    /// ข้อความ AI → ลับ เว้นแต่ข้อความที่ไม่ใช่ AI ล่าสุดก่อนหน้าใน session เดียวกันเป็นของผู้เล่น
    /// (`/actions` เขียนข้อความผู้เล่นก่อน แล้วค่อยเขียนคำตอบ AI ตามหลัง) · migration รันด้วย role
    /// `dndez` ที่เป็น superuser (bypass RLS) จึง UPDATE ได้ครบแม้ตารางเป็น FORCE RLS
    ///
    /// ## 2. `recap_snapshots` เขียนได้เฉพาะ DM
    /// เดิมได้ `campaign_isolation` FOR ALL จาก loop ของ migration `RowLevelSecurity` = สมาชิกทุกคนเขียน
    /// recap ได้ — ไม่เคยเป็นปัญหาเพราะไม่มีใครเขียนตารางนี้เลย แต่ตอนนี้ AI จะเขียน `dm_version_text` ที่มี
    /// ความลับของ DM ลงไป · แยกเป็น `member_read` + `dm_write` (แบบ `AddAutonomySettingDmOnlyRls`) เพราะ
    /// ผู้เล่น**ควร**อ่านแถวได้ (เพื่อเห็น `player_version_text`) · ⚠️ RLS แยกระดับคอลัมน์ไม่ได้ —
    /// `dm_version_text` ในแถวเดียวกันจึงกันที่ชั้น endpoint (`SessionRecapEndpoints` projection) แบบเดียว
    /// กับที่ HP ของ NPC กันด้วย `CharacterStatsVisibility`
    /// </summary>
    public partial class AddMessageDmPrivateAndRecapDmWrite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "dm_private",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE messages SET dm_private = true WHERE author_type = 'Dm';

                UPDATE messages m SET dm_private = true
                WHERE m.author_type = 'Ai'
                  AND (SELECT p.author_type FROM messages p
                       WHERE p.session_id = m.session_id
                         AND p.author_type <> 'Ai'
                         AND p.created_at <= m.created_at
                       ORDER BY p.created_at DESC
                       LIMIT 1) IS DISTINCT FROM 'Player';

                DROP POLICY IF EXISTS campaign_isolation ON messages;

                CREATE POLICY member_public_dm_private ON messages
                    USING (
                        EXISTS (SELECT 1 FROM sessions s
                                WHERE s.id = messages.session_id AND s.campaign_id = app_current_campaign())
                        AND app_is_member()
                        AND (NOT dm_private OR app_is_dm()))
                    WITH CHECK (
                        EXISTS (SELECT 1 FROM sessions s
                                WHERE s.id = messages.session_id AND s.campaign_id = app_current_campaign())
                        AND app_is_member()
                        AND (NOT dm_private OR app_is_dm()));

                DROP POLICY IF EXISTS campaign_isolation ON recap_snapshots;

                CREATE POLICY member_read ON recap_snapshots
                    FOR SELECT
                    USING (
                        EXISTS (SELECT 1 FROM sessions s
                                WHERE s.id = recap_snapshots.session_id AND s.campaign_id = app_current_campaign())
                        AND app_is_member());

                CREATE POLICY dm_write ON recap_snapshots
                    FOR ALL
                    USING (
                        EXISTS (SELECT 1 FROM sessions s
                                WHERE s.id = recap_snapshots.session_id AND s.campaign_id = app_current_campaign())
                        AND app_is_dm())
                    WITH CHECK (
                        EXISTS (SELECT 1 FROM sessions s
                                WHERE s.id = recap_snapshots.session_id AND s.campaign_id = app_current_campaign())
                        AND app_is_dm());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS dm_write ON recap_snapshots;
                DROP POLICY IF EXISTS member_read ON recap_snapshots;

                CREATE POLICY campaign_isolation ON recap_snapshots
                    USING (EXISTS (SELECT 1 FROM sessions s
                                   WHERE s.id = recap_snapshots.session_id AND s.campaign_id = app_current_campaign())
                           AND app_is_member())
                    WITH CHECK (EXISTS (SELECT 1 FROM sessions s
                                        WHERE s.id = recap_snapshots.session_id AND s.campaign_id = app_current_campaign())
                                AND app_is_member());

                DROP POLICY IF EXISTS member_public_dm_private ON messages;

                CREATE POLICY campaign_isolation ON messages
                    USING (EXISTS (SELECT 1 FROM sessions s
                                   WHERE s.id = messages.session_id AND s.campaign_id = app_current_campaign())
                           AND app_is_member())
                    WITH CHECK (EXISTS (SELECT 1 FROM sessions s
                                        WHERE s.id = messages.session_id AND s.campaign_id = app_current_campaign())
                                AND app_is_member());
                """);

            migrationBuilder.DropColumn(
                name: "dm_private",
                table: "messages");
        }
    }
}

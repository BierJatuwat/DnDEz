using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint F งานที่ 1 — join campaign flow ผ่านโค้ดที่ DM กำหนดเอง (ตัดสิน 2026-08-30: ไม่ใช้
    /// `GameSession.JoinCode` เดิมที่เป็น per-session/CSPRNG — โค้ดนี้ผูกกับ`campaign` และ DM เป็น
    /// คนตั้งเอง ใช้ join ได้ทั้งแคมเปญและทุก session ในนั้น) มีสองส่วน:
    ///
    ///  1. **RLS UPDATE policy ใหม่ของ `campaigns`** — migration `FixCampaignListRlsPolicy`
    ///     (2026-08-25) จงใจทิ้ง `campaigns` ไว้โดยไม่มี UPDATE policy เลย พร้อมคอมเมนต์ไว้ตรง ๆ ว่า
    ///     "เพิ่ม policy ทีหลังได้เมื่อมี use case จริง (เช่น 'DM แก้ชื่อแคมเปญ')" — นี่คือ use case นั้น
    ///     ไม่ใช่ circular-bootstrap แบบตอนสร้างแคมเปญ (DM เป็นสมาชิก/owner อยู่แล้วตอนจะแก้) จึงใช้
    ///     RLS policy ธรรมดาได้เลย ไม่ต้องพึ่ง SECURITY DEFINER function
    ///
    ///  2. **`app_join_campaign()` (SECURITY DEFINER)** — ต้องใช้เพราะเป็น circular bootstrap แบบ
    ///     เดียวกับตอนสร้างแคมเปญทุกประการ: INSERT แถวใหม่ใน `campaign_members` โดน `WITH CHECK
    ///     (campaign_id = app_current_campaign() AND app_is_member())` ของ policy `campaign_isolation`
    ///     บล็อกเสมอ (ต้องเป็นสมาชิกอยู่แล้วก่อนถึงจะ insert แถวพิสูจน์ความเป็นสมาชิกได้ — วนไม่จบ)
    ///     ⚠️ **user_id ต้องมาจาก `app_current_user_id()` เท่านั้น ห้ามรับเป็น parameter จากภายนอก**
    ///     (หลักการเดียวกับ `app_create_campaign()` — กัน backdoor ให้ join แทนคนอื่น) · join ซ้ำ
    ///     (เป็นสมาชิกอยู่แล้วไม่ว่า role ไหน) เป็น no-op คืน campaign_id เดิมเฉย ๆ ไม่ throw ไม่ insert
    ///     ซ้ำ (กัน unique constraint violation กลายเป็น 500 ตอน DM กด join code ของแคมเปญตัวเอง)
    /// </summary>
    public partial class AddCampaignJoinCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "join_code",
                table: "campaigns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_join_code",
                table: "campaigns",
                column: "join_code",
                unique: true,
                filter: "join_code IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE POLICY campaign_owner_update ON campaigns
                    FOR UPDATE
                    USING (owner_user_id = app_current_user_id())
                    WITH CHECK (owner_user_id = app_current_user_id());

                CREATE OR REPLACE FUNCTION app_join_campaign(p_join_code text, p_membership_id uuid)
                    RETURNS uuid
                    LANGUAGE plpgsql SECURITY DEFINER AS
                $$
                DECLARE
                    v_user_id uuid;
                    v_campaign_id uuid;
                BEGIN
                    v_user_id := app_current_user_id();

                    IF v_user_id IS NULL THEN
                        RAISE EXCEPTION
                            'app_join_campaign: app.user_id ยังไม่ถูกตั้งค่า (ต้อง SET LOCAL app.user_id ก่อนเรียก)';
                    END IF;

                    SELECT id INTO v_campaign_id FROM campaigns WHERE join_code = p_join_code;
                    IF v_campaign_id IS NULL THEN
                        RETURN NULL;
                    END IF;

                    IF EXISTS (
                        SELECT 1 FROM campaign_members
                        WHERE campaign_id = v_campaign_id AND user_id = v_user_id
                    ) THEN
                        RETURN v_campaign_id;
                    END IF;

                    INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
                    VALUES (p_membership_id, v_campaign_id, v_user_id, 'Player', now());

                    RETURN v_campaign_id;
                END;
                $$;

                GRANT EXECUTE ON FUNCTION app_join_campaign(text, uuid) TO dndez_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS app_join_campaign(text, uuid);
                DROP POLICY IF EXISTS campaign_owner_update ON campaigns;
                """);

            migrationBuilder.DropIndex(
                name: "ix_campaigns_join_code",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "join_code",
                table: "campaigns");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// แก้ bootstrap gap ที่พบตอนทำ Sprint A Part 1/2 (ดู docs/db-schema.md "ช่องโหว่ที่พบต่อเนื่อง"):
    /// สร้างแคมเปญใหม่ผ่าน role dndez_app ไม่ได้เลยด้วย INSERT ตรง ๆ เพราะ WITH CHECK ของทั้ง
    /// campaigns (id = app_current_campaign() AND app_is_member()) และ campaign_members
    /// (campaign_id = app_current_campaign() AND app_is_member()) ต้องการ app_is_member() เป็นจริง
    /// ก่อน — แต่แถวที่จะพิสูจน์ความเป็นสมาชิกคือแถวที่กำลังจะสร้างพอดี (circular)
    ///
    /// แก้ด้วย SECURITY DEFINER function รูปแบบเดียวกับ app_is_member()/app_is_dm() ที่มีอยู่แล้ว —
    /// function ถูกสร้างโดย role dndez (owner) ซึ่งเป็นเจ้าของทั้ง campaigns และ campaign_members
    /// อยู่แล้ว เมื่อ execute ด้วย SECURITY DEFINER จะรันด้วยสิทธิ์ของ dndez ทำให้ INSERT ทั้งสองตาราง
    /// bypass RLS ของตัวเองได้ (owner ไม่ติด policy ใด ๆ) — เหมือนหลักการเดียวกับที่ app_is_member()
    /// ใช้ query campaign_members ได้โดยไม่ชนกับ policy ที่ตัวมันเองเอาไปใช้ตรวจ
    ///
    /// ⚠️ จุดที่ต้องระวังเพื่อไม่ให้ function นี้กลายเป็น backdoor: owner_user_id ของแคมเปญ +
    /// user_id ของสมาชิก DM คนแรก **ต้องมาจาก app_current_user_id() เท่านั้น ห้ามรับเป็น parameter
    /// จากภายนอกเด็ดขาด** — ถ้ารับจากภายนอกได้ ใครก็เรียกสร้างแคมเปญที่ "เป็นเจ้าของ" ให้ user คนอื่น
    /// ได้ทันที (function bypass RLS ไปแล้ว ไม่มีอะไรมาเช็คซ้ำ) — RAISE EXCEPTION ทันทีถ้าไม่ได้ตั้ง
    /// app.user_id (SET LOCAL) มาก่อนเรียก กัน caller เผลอเรียกนอก session ที่ authenticate แล้ว
    /// </summary>
    public partial class AddCampaignCreationBootstrapFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION app_create_campaign(
                    p_campaign_id uuid,
                    p_name text,
                    p_mode text,
                    p_rules_system text,
                    p_dice_mode text,
                    p_death_save_visibility text,
                    p_membership_id uuid
                ) RETURNS uuid
                    LANGUAGE plpgsql SECURITY DEFINER AS
                $$
                DECLARE
                    v_owner_user_id uuid;
                BEGIN
                    v_owner_user_id := app_current_user_id();

                    IF v_owner_user_id IS NULL THEN
                        RAISE EXCEPTION
                            'app_create_campaign: app.user_id ยังไม่ถูกตั้งค่า (ต้อง SET LOCAL app.user_id ก่อนเรียก)';
                    END IF;

                    INSERT INTO campaigns (
                        id, owner_user_id, name, mode, rules_system, dice_mode,
                        death_save_visibility, created_at
                    )
                    VALUES (
                        p_campaign_id, v_owner_user_id, p_name, p_mode, p_rules_system, p_dice_mode,
                        p_death_save_visibility, now()
                    );

                    INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
                    VALUES (p_membership_id, p_campaign_id, v_owner_user_id, 'Dm', now());

                    RETURN p_campaign_id;
                END;
                $$;

                GRANT EXECUTE ON FUNCTION app_create_campaign(
                    uuid, text, text, text, text, text, uuid
                ) TO dndez_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS app_create_campaign(uuid, text, text, text, text, text, uuid);
                """);
        }
    }
}

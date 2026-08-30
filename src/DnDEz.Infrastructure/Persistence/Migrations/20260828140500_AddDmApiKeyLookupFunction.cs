using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint E Part 3 — player action input ต้องเรียกออร์เคสตราชั้นเดียวกับ `/consult` แต่ผู้เรียก
    /// (ผู้เล่น) ไม่ใช่เจ้าของ API key ที่ต้องใช้จริง (BYOK ผูกกับ DM เท่านั้น ดู
    /// `player-side-design.md` §7/PROJECT-CONTEXT §2 "Quick-play players ใช้ key ของ DM เท่านั้น")
    ///
    /// `api_keys` มี RLS `own_rows_only` (`user_id = app_current_user_id()`) — query ธรรมดาหา
    /// "key ของ DM" ตอนที่ผู้เรียกจริงคือผู้เล่นจะโดนกรองว่างเปล่าเสมอ ไม่ว่า C# WHERE จะเขียนถูกแค่ไหน
    /// (เหตุผลเดิมที่ทำให้ ConsultEndpoints จำกัดไว้แค่ DM — ดู doc comment ของคลาสนั้น) แก้ด้วย
    /// SECURITY DEFINER function รูปแบบเดียวกับ `app_create_campaign()`/`app_is_member_of()` —
    /// รันด้วยสิทธิ์ owner (`dndez`) จึง bypass RLS ของ `api_keys` ได้ แต่ **เช็คสิทธิ์เองข้างในฟังก์ชัน**
    /// (ไม่ใช่แค่พึ่ง C# เช็คก่อนเรียก) เป็น defense-in-depth ชั้นที่สอง:
    ///  • ใช้ `app_is_member_of(p_campaign_id)` ที่มีอยู่แล้ว (จาก FixCampaignListRlsPolicy) ยืนยันว่า
    ///    ผู้เรียกเป็นสมาชิกแคมเปญจริง ไม่ใช่ user คนไหนก็เรียกได้
    ///  • คืนค่าแค่ ciphertext (`encrypted_key`) เท่านั้น ไม่คืน id/status ใด ๆ — เข้ารหัสด้วย ASP.NET
    ///    Data Protection ที่ decrypt ได้เฉพาะ server เท่านั้นอยู่แล้ว เป็นเกราะชั้นที่สาม
    ///  • ไม่ใช่สมาชิก / ไม่มี key active ของ DM เลย → คืน NULL เหมือนกันทั้งคู่ (ไม่แยกเคส กัน
    ///    enumeration ตาม convention เดียวกับที่ล็อกไว้ทั้งระบบว่า "แถวหาย = 404/NULL เดียวกันเสมอ")
    /// </summary>
    public partial class AddDmApiKeyLookupFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION app_get_dm_api_key(p_campaign_id uuid, p_provider text)
                    RETURNS text
                    LANGUAGE plpgsql STABLE SECURITY DEFINER AS
                $$
                DECLARE
                    v_dm_user_id uuid;
                    v_encrypted_key text;
                BEGIN
                    IF NOT app_is_member_of(p_campaign_id) THEN
                        RETURN NULL;
                    END IF;

                    SELECT owner_user_id INTO v_dm_user_id FROM campaigns WHERE id = p_campaign_id;
                    IF v_dm_user_id IS NULL THEN
                        RETURN NULL;
                    END IF;

                    SELECT k.encrypted_key INTO v_encrypted_key
                    FROM api_keys k
                    WHERE k.user_id = v_dm_user_id
                      AND k.provider = p_provider
                      AND k.status = 'Active'
                    LIMIT 1;

                    RETURN v_encrypted_key;
                END;
                $$;

                GRANT EXECUTE ON FUNCTION app_get_dm_api_key(uuid, text) TO dndez_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS app_get_dm_api_key(uuid, text);
                """);
        }
    }
}

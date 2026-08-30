using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ปิดช่องโหว่ CVE-2018-1058 (search_path hijacking ของฟังก์ชัน SECURITY DEFINER) ด้วย
    /// REVOKE TEMPORARY + pin search_path บน helper function 8 ตัว
    /// (ตัดสิน 2026-08-30) เหตุผล:
    ///
    ///  1. **ช่องโหว่ — พิสูจน์จริงบน DB dev แล้ว ไม่ใช่สมมติฐาน** — ฟังก์ชัน `app_*` อ้างชื่อตาราง
    ///     แบบไม่ qualify (`FROM campaign_members`) และไม่มี `search_path` ที่ pin ไว้
    ///     (`pg_proc.proconfig IS NULL` ทั้ง 8 ตัว) · role `dndez_app` มีสิทธิ์ TEMP จึงทำได้ว่า:
    ///     <code>
    ///     CREATE TEMP TABLE campaign_members (campaign_id uuid, user_id uuid, role text);
    ///     INSERT INTO campaign_members VALUES ('&lt;โต๊ะไหนก็ได้&gt;', '&lt;ตัวเอง&gt;', 'Dm');
    ///     </code>
    ///     เพราะ Postgres ค้น `pg_temp` **ก่อน** schema อื่นเสมอ `app_is_dm()` จึงอ่านตารางปลอม
    ///     แล้วคืน true · ทดลองจริง: ผู้เล่นที่มี role `'Player'` ยกตัวเองเป็น DM แล้ว INSERT
    ///     `autonomy_settings` (ตาราง DM-only) สำเร็จ และแถวลงจริงใน `public.autonomy_settings`
    ///
    ///  2. **ทำไมนี่คือ "กุญแจผี" ไม่ใช่บั๊กของตารางเดียว** — วัดจาก `pg_policies` ตอนแก้:
    ///     `app_is_dm()` ถูกใช้ใน policy ของ **3 ตาราง** ส่วน helper ตระกูล `app_is_*`/`app_current_*`
    ///     รวมกันถูกใช้ใน **ทุกตารางที่มี RLS policy — 27/27 ตาราง (รวม 30 policy)** — การ hijack
    ///     ฟังก์ชันพวกนี้จึงล้ม RLS ได้ทั้งระบบพร้อมกัน ไม่ใช่แค่จุดเดียว
    ///
    ///  3. **ทำไมต้องแก้สองชั้น ไม่ใช่ชั้นเดียว (defense in depth)** — REVOKE ตัดพื้นที่โจมตีทิ้ง
    ///     ทั้งหมด (สร้าง temp object ไม่ได้เลย) ส่วนการ pin `search_path` ยังกันได้อยู่แม้วันหลัง
    ///     มีคนคืนสิทธิ์ TEMPORARY กลับมา (migration ใหม่ / extension ที่ต้องใช้ temp table /
    ///     DBA หวังดี) · ถ้าเหลือชั้นเดียว การถอยกลับเงียบ ๆ ครั้งเดียวก็เปิดช่องโหว่คืนทันที
    ///
    ///  4. ⚠️ **กับดักที่ 1 — `REVOKE ... FROM dndez_app` เป็น no-op เงียบ ๆ** ·
    ///     `pg_database.datacl` ของ `dndez` เป็น NULL (ACL ดีฟอลต์) แปลว่าสิทธิ์ TEMP ถือโดย
    ///     **PUBLIC** ไม่ได้ grant ตรงให้ role · ทดลองแล้ว: คำสั่งคืน `REVOKE` ไม่ error แต่
    ///     `has_database_privilege('dndez_app','dndez','TEMP')` ยังเป็น true อยู่ → ต้อง
    ///     `REVOKE TEMPORARY ... FROM PUBLIC` เท่านั้น · และ ACL ของ PUBLIC คือ `=Tc/dndez`
    ///     ถือทั้ง `T` (TEMPORARY) และ `c` (CONNECT) จึงต้อง revoke **เฉพาะ TEMPORARY**
    ///     ห้ามใช้ `REVOKE ALL ... FROM PUBLIC` เพราะจะพราก CONNECT ไปด้วย = API ต่อ DB ไม่ได้ทั้งระบบ
    ///     · ⚠️ **ขอบเขตผลกระทบ**: การ revoke จาก PUBLIC มีผลกับ **ทุก role ที่ไม่ใช่ superuser/owner**
    ///     ในฐานข้อมูลนี้ ไม่ใช่แค่ `dndez_app` · วันหลังถ้าเพิ่ม role สำหรับ analytics/report หรือ
    ///     extension ที่ต้องใช้ temp table ขึ้นมา มันจะพังด้วย `permission denied to create temporary
    ///     tables` แล้วสาเหตุจะย้อนกลับมาถึงไฟล์นี้ยาก — ถ้าเจอ ให้ GRANT TEMPORARY เฉพาะ role นั้น
    ///     เป็นราย ๆ ไป ห้าม GRANT คืนให้ PUBLIC ทั้งก้อน (จะเปิดช่องโหว่กลับมาเหมือนเดิม)
    ///
    ///     ⚠️ **`Down()` คืนสิทธิ์เท่าเดิมแต่ ACL ไม่เหมือนเดิมเป๊ะ** — ก่อน migration `datacl`
    ///     เป็น NULL (ดีฟอลต์) หลัง `Up()` แล้ว `Down()` มันจะกลายเป็น ACL ที่เขียนไว้ชัดเจน
    ///     (`{=Tc/dndez,dndez=CTc/dndez}`) ซึ่ง**ให้สิทธิ์เท่ากัน**แต่ไม่ใช่ NULL แล้ว · ตรวจแล้วว่า
    ///     ใช้งานได้ปกติ (TEMP=true, CONNECT=true) แค่บอกไว้ไม่ให้ใครตกใจตอนเทียบ `datacl`
    ///
    ///  5. ⚠️ **กับดักที่ 2 — การ "ไม่ใส่ pg_temp" ไม่ได้แปลว่าตัดมันออก** · ผลทดลองจริง:
    ///     <list type="bullet">
    ///       <item>ไม่ตั้ง search_path เลย → โดน shadow</item>
    ///       <item>`SET search_path = public` (ละ pg_temp) → **ยังโดน shadow**</item>
    ///       <item>`SET search_path = pg_temp, public` → โดน shadow</item>
    ///       <item>`SET search_path = pg_catalog, public, pg_temp` (pg_temp ท้ายสุด) → ปลอดภัย</item>
    ///     </list>
    ///     Postgres แทรก `pg_temp` ไว้ **หน้าสุด** โดยปริยายถ้าไม่ได้ระบุตำแหน่งมันเอง ทางเดียว
    ///     ที่ลดอันดับมันได้คือ **ระบุไว้ท้ายสุดอย่างชัดเจน** ตรงตามที่ Postgres manual แนะนำใน
    ///     "Writing SECURITY DEFINER Functions Safely" · เขียน `SET search_path = public` เฉย ๆ
    ///     จะได้ "ความมั่นใจปลอม" ซึ่งแย่กว่าไม่แก้เลย
    ///
    ///  6. **ทำไม `app_current_campaign()` / `app_current_user_id()` ถูก pin ด้วย** ทั้งที่ไม่ใช่
    ///     SECURITY DEFINER — เพราะถูกเรียกจาก **ข้างใน** ฟังก์ชัน SECURITY DEFINER อีกที
    ///     (`app_is_dm()` เรียกทั้งสองตัว) และทดลองแล้วว่า `dndez_app` รัน
    ///     `CREATE FUNCTION pg_temp.app_current_user_id()` ได้จริง → CVE-2018-1058 ครอบ
    ///     **ฟังก์ชัน** ไม่ใช่แค่ตาราง ตัวที่ไม่ใช่ SECURITY DEFINER จึงเป็นทางอ้อมเข้าตัวที่ใช่ได้
    ///
    ///  7. **ใช้ `format('... %I ...', current_database())` แทน hardcode ชื่อ DB** — เพื่อให้
    ///     migration ใช้ได้กับ environment ที่ตั้งชื่อฐานข้อมูลต่างออกไป (CI/staging) ·
    ///     `%I` = `quote_ident()` ปลอดภัยจาก SQL injection ของชื่อ identifier
    /// </summary>
    public partial class HardenSecurityDefinerSearchPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
                END
                $$;

                ALTER FUNCTION app_is_dm()            SET search_path = pg_catalog, public, pg_temp;
                ALTER FUNCTION app_is_member()        SET search_path = pg_catalog, public, pg_temp;
                ALTER FUNCTION app_is_member_of(uuid) SET search_path = pg_catalog, public, pg_temp;
                ALTER FUNCTION app_current_campaign() SET search_path = pg_catalog, public, pg_temp;
                ALTER FUNCTION app_current_user_id()  SET search_path = pg_catalog, public, pg_temp;
                ALTER FUNCTION app_create_campaign(uuid, text, text, text, text, text, uuid) SET search_path = pg_catalog, public, pg_temp;
                ALTER FUNCTION app_get_dm_api_key(uuid, text) SET search_path = pg_catalog, public, pg_temp;
                ALTER FUNCTION app_join_campaign(text, uuid)  SET search_path = pg_catalog, public, pg_temp;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER FUNCTION app_is_dm()            RESET search_path;
                ALTER FUNCTION app_is_member()        RESET search_path;
                ALTER FUNCTION app_is_member_of(uuid) RESET search_path;
                ALTER FUNCTION app_current_campaign() RESET search_path;
                ALTER FUNCTION app_current_user_id()  RESET search_path;
                ALTER FUNCTION app_create_campaign(uuid, text, text, text, text, text, uuid) RESET search_path;
                ALTER FUNCTION app_get_dm_api_key(uuid, text) RESET search_path;
                ALTER FUNCTION app_join_campaign(text, uuid)  RESET search_path;

                DO $$
                BEGIN
                    EXECUTE format('GRANT TEMPORARY ON DATABASE %I TO PUBLIC', current_database());
                END
                $$;
                """);
        }
    }
}

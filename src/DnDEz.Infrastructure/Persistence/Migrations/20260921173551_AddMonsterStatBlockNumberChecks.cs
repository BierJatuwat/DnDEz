using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// CHECK ของตัวเลขบน `monster_stat_blocks` — ใส่ตอนที่ **เพิ่งมี endpoint ให้ DM แก้ค่าเองได้**
    /// (`PATCH /campaigns/{cid}/monsters/{id}`) ก่อนหน้านี้ค่าทั้งหมดมาจากการคัดลอกคลัง SRD/import
    /// จึงไม่มีทางเป็นค่าประหลาด · code review ของ Sprint H Part 7 เคยเสนอให้ใส่ CHECK ของ reach_feet
    /// แล้ว owner ปฏิเสธด้วยเหตุผลว่า "ยังไม่มี endpoint เขียนคอลัมน์นี้" — ตอนนี้เหตุผลนั้นหมดไปแล้ว
    ///
    /// `speed_feet = 0` ถูกต้องได้ (สิ่งมีชีวิตที่ขยับเองไม่ได้) แต่ `reach_feet` ต่ำกว่า 5 ไม่มีทางถูกตามกฎ:
    /// SRD 5.2 "Reach" — *"A creature has a 5-foot reach"* · ค่านี้ถูกใช้ตัดสิน opportunity attack จริง
    /// (Sprint H Part 7) ถ้าหลุดเป็น 0 ไปได้ มอนสเตอร์จะไม่มีวันสวนกลับโดยไม่มีใครรู้ว่าทำไม
    ///
    /// ไม่ใช่ตารางใหม่ — เพิ่ม CHECK บนตารางเดิมที่มี RLS อยู่แล้ว (ENABLE + FORCE, policy
    /// `campaign_isolation` FOR ALL) ตรวจกับ pg_class/pg_policies แล้วตอนทำ migration `AddMonsterSrdKey`
    /// </summary>
    public partial class AddMonsterStatBlockNumberChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_monster_stat_block_numbers",
                table: "monster_stat_blocks",
                sql: "armor_class > 0 AND hit_points > 0 AND speed_feet >= 0 AND reach_feet >= 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_monster_stat_block_numbers",
                table: "monster_stat_blocks");
        }
    }
}

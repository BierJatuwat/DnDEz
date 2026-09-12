using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint H Part 7 — `monster_stat_blocks` ได้ speed + reach (spatial-play-design §2.4)
    ///
    /// **ทำไมต้องมี**: `MovementSceneContext` ใช้ `DefaultReachFeet = 5` กับทุกตัว มอนสเตอร์เอื้อมยาวจึงถูก
    /// คิดเป็น 5 ฟุตมาตลอด ทั้งที่ engine รองรับค่าอื่นอยู่แล้ว (OpportunityAttackResolverTests มีเคส 10 ฟุต)
    /// — ขาดแค่ "ที่เก็บข้อมูล" ไม่ใช่ขาดกฎ
    ///
    /// **ดีฟอลต์ 30/5 ไม่ใช่ 0**: แถวที่ import มาก่อน Part นี้ต้องใช้งานได้ทันทีโดยไม่ต้องไล่แก้ — 30 ฟุต
    /// คือ speed มาตรฐานของสิ่งมีชีวิตขนาดกลาง และ SRD 5.2 "Reach": *"A creature has a 5-foot reach"*
    ///
    /// **ไม่ใช่ตารางใหม่** — เพิ่มคอลัมน์บนตารางเดิมที่มี RLS อยู่แล้ว (ENABLE + FORCE, policy
    /// `campaign_isolation` FOR ALL — owner ตรวจด้วย pg_class/pg_policies ก่อนเขียน migration นี้)
    /// จึงไม่ต้องสร้าง policy ใหม่ตามกฎถาวรของตารางใหม่ใน CLAUDE.md
    ///
    /// ## ยังไม่ได้ทำในรอบนี้ (ตั้งใจ เขียนไว้ไม่ให้เงียบ)
    /// - **พลังชีวิตรายตัวของมอนสเตอร์**: `monster_stat_blocks` เป็นแม่แบบระดับแคมเปญ ไม่ใช่ instance —
    ///   ก็อบลิน 3 ตัวบนแผนที่ใช้แถวเดียวกัน · `POST .../attacks` จึงยังรับเฉพาะเป้าที่เป็นตัวละคร
    ///   affordance บอกชนิดเป้ามาด้วย (`targetEntityType`) เพื่อให้หน้าจอไม่เสนอปุ่มที่กดแล้วพัง
    /// - **endpoint ให้ DM แก้ speed/reach**: ตอนนี้ตั้งค่าได้ตอน import หรือแก้ผ่าน DB เท่านั้น
    /// - **`actions` jsonb ยังไม่เชื่อมกับ action catalog**: มอนสเตอร์ยังไม่มี "ท่า" ที่ engine คำนวณให้ได้
    ///   (ทางที่เล็กที่สุดที่ใช้ได้จริงในรอบนี้คือ reach ซึ่งเป็นสิ่งเดียวที่กฎการเดิน/OA ต้องใช้จริง ๆ)
    /// </summary>
    public partial class AddMonsterSpeedAndReach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "reach_feet",
                table: "monster_stat_blocks",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "speed_feet",
                table: "monster_stat_blocks",
                type: "integer",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reach_feet",
                table: "monster_stat_blocks");

            migrationBuilder.DropColumn(
                name: "speed_feet",
                table: "monster_stat_blocks");
        }
    }
}

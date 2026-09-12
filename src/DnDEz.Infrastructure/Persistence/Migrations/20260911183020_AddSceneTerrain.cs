using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint H Part 5 — `scenes.terrain` (jsonb) เก็บพื้นจากแผนที่: พื้นเดินยาก + กำแพง
    /// (spatial-play-design §3.3 ชั้น "พื้นจากแผนที่") รูปร่าง <c>{"difficult":[[x,y],...],"blocked":[[x,y],...]}</c>
    ///
    /// **ทำไมคอลัมน์ใหม่ ไม่ขยาย `scenes.grid`** (§3.3 เขียนไว้ว่า grid): endpoint `PATCH .../grid`
    /// สร้าง grid ใหม่ทั้งก้อนทุกครั้ง — ถ้าพื้นอยู่ใน grid การจัดกริดครั้งเดียวจะลบกำแพงทั้งฉากทิ้งเงียบ ๆ
    /// (ล็อกด้วยเทสต์ <c>Recalibrating_the_grid_does_not_wipe_painted_terrain</c>)
    ///
    /// **RLS: ไม่ใช่ตารางใหม่** — เพิ่มคอลัมน์บน `scenes` ที่มี RLS อยู่แล้ว (ENABLE + FORCE, policy
    /// `campaign_isolation` — owner query `pg_class`/`pg_policies` ดูเองก่อนเขียน migration นี้) จึงไม่ต้อง
    /// สร้าง policy ใหม่ตามกฎถาวรของตารางใหม่ใน CLAUDE.md · ⚠️ แต่ policy นั้นเป็น FOR ALL สำหรับสมาชิก
    /// ทุกคน → การจำกัดให้ **DM เท่านั้น** เขียนพื้นได้อยู่ที่ชั้น endpoint (SceneTerrainEndpoints) ไม่ใช่ที่ DB
    /// เหมือน grid/fog เดิม
    ///
    /// ชั้นพื้นจาก effect (Web/Grease ที่ผูก encounter) ยังไม่ทำใน Part นี้ — ดู SceneTerrainSerialization
    /// </summary>
    public partial class AddSceneTerrain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "terrain",
                table: "scenes",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "terrain",
                table: "scenes");
        }
    }
}

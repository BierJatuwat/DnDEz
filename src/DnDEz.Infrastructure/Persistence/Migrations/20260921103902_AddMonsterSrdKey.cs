using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// `monster_stat_blocks.srd_key` — บอกว่าแม่แบบนี้คัดลอกมาจากคลัง SRD (`srd_monsters`) ตัวไหน · ใช้กันซ้ำต่อแคมเปญ
    /// และแสดงที่มา (CC-BY-4.0) · **ไม่ใช่ตารางใหม่**: เพิ่มคอลัมน์บนตารางเดิมที่มี RLS อยู่แล้ว (ENABLE + FORCE,
    /// policy `campaign_isolation` FOR ALL — ตรวจด้วย pg_class/pg_policies หลัง apply migration นี้) จึงไม่ต้องสร้าง policy ใหม่
    /// </summary>
    public partial class AddMonsterSrdKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "srd_key",
                table: "monster_stat_blocks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_monster_stat_blocks_campaign_id_srd_key",
                table: "monster_stat_blocks",
                columns: new[] { "campaign_id", "srd_key" },
                unique: true,
                filter: "srd_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_monster_stat_blocks_campaign_id_srd_key",
                table: "monster_stat_blocks");

            migrationBuilder.DropColumn(
                name: "srd_key",
                table: "monster_stat_blocks");
        }
    }
}

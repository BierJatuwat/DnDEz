using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// เพิ่ม `characters.npc_stats_visible_to_party` (Sprint F งานที่ 7) — ดู doc comment ของ
    /// `Character.NpcStatsVisibleToParty` ใน Domain entity
    ///
    /// ⚠️ **ตัด `xmin` AddColumn/DropColumn ที่ scaffold มาด้วยออกโดยตั้งใจ** — `dotnet ef migrations
    /// add` เจอ pending model change สำหรับ `death_save_states.xmin` (จาก
    /// `e.Property&lt;uint&gt;("xmin").IsRowVersion()` ที่ตั้งไว้ตอนแก้ concurrency race ของงานที่ 5)
    /// แล้ว scaffold `AddColumn&lt;uint&gt;(name: "xmin", ...)` ให้ — **นี่คือ bug ที่รู้จักแล้วของ
    /// Npgsql.EntityFrameworkCore.PostgreSQL** (ยืนยันจาก GitHub issue npgsql/efcore.pg#3854:
    /// migration generator ไม่ suppress DDL ให้ system column แม้ `IsRowVersion()` จะเป็นวิธี config
    /// ที่ถูกต้องตามเอกสารทางการตั้งแต่ v7.0 เป็นต้นมา) `xmin` เป็น system column ของ Postgres ที่มีอยู่
    /// แล้วในทุกตารางโดยไม่ต้องสร้าง — apply migration ที่มี `AddColumn` ตัวนี้จริงจะพังด้วย
    /// "column 'xmin' conflicts with a system column name" การ map ผ่าน `IsRowVersion()` ใน
    /// `DnDEzDbContext.cs` ทำงานถูกต้องอยู่แล้วที่ runtime (EF อ่าน system column ตรง ๆ ไปใช้เทียบใน
    /// WHERE clause ตอน UPDATE) ไม่ต้องมี DDL ใด ๆ เลย
    /// </summary>
    public partial class AddNpcStatsVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true ให้ตรงกับ default ฝั่ง C# (Character.NpcStatsVisibleToParty)
            // เจตนา: ไม่มีตัวละคร ControlledByDm=true อยู่ในระบบตอนนี้เลย (hardcode false มาตลอด
            // ก่อนงานนี้) แต่ตั้งให้ตรงกันไว้เผื่อมีใคร INSERT ตรงข้าม C# layer ในอนาคต
            migrationBuilder.AddColumn<bool>(
                name: "npc_stats_visible_to_party",
                table: "characters",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "npc_stats_visible_to_party",
                table: "characters");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint G Part 2 (design doc §12) — บังคับว่าแคมเปญหนึ่งมี `story_beats.progress = 'Active'`
    /// ได้ทีละแถวเท่านั้น ระดับ DB (ไม่ใช่แค่ endpoint logic ที่ demote ตัวเก่าก่อน promote ตัวใหม่ —
    /// partial unique index นี้เป็นตาข่ายรองกันเคส race condition/insert ตรงข้าม endpoint)
    /// pattern เดียวกับ `ix_scenes_one_active_per_session` ที่มีอยู่แล้วตั้งแต่ InitialSchema
    /// </summary>
    public partial class AddStoryBeatOneActivePerCampaignIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_story_beats_one_active_per_campaign",
                table: "story_beats",
                column: "campaign_id",
                unique: true,
                filter: "progress = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_story_beats_one_active_per_campaign",
                table: "story_beats");
        }
    }
}

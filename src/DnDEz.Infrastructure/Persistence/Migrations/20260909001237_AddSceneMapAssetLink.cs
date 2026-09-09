using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint G Part 4 — เพิ่ม `scenes.map_asset_id` (nullable, SetNull) แทนการพึ่ง
    /// `scenes.background_image_url` ที่มีอยู่แล้ว — signed URL อายุสั้น (นาที) เก็บลง DB ตรง ๆ จะ
    /// หมดอายุก่อนมีคนเปิดดูจริง (session เล่นเป็นชั่วโมง) จึงต้องเก็บ "อ้างอิงไปยังไฟล์" แทน
    /// "URL ที่ใช้ตอนนี้" แล้ว generate signed URL สดทุกครั้งที่ GET scene (ดู
    /// <c>src/DnDEz.Api/Endpoints/SceneEndpoints.cs</c>) `background_image_url` เดิมปล่อยไว้เฉย ๆ
    /// ไม่ลบ (ไม่ได้ใช้ต่อ แต่ยังไม่คุ้มเสียเวลาทำ migration ลบคอลัมน์สำหรับ Part นี้)
    /// </summary>
    public partial class AddSceneMapAssetLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "map_asset_id",
                table: "scenes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_scenes_map_asset_id",
                table: "scenes",
                column: "map_asset_id");

            migrationBuilder.AddForeignKey(
                name: "fk_scenes_map_assets_map_asset_id",
                table: "scenes",
                column: "map_asset_id",
                principalTable: "map_assets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_scenes_map_assets_map_asset_id",
                table: "scenes");

            migrationBuilder.DropIndex(
                name: "ix_scenes_map_asset_id",
                table: "scenes");

            migrationBuilder.DropColumn(
                name: "map_asset_id",
                table: "scenes");
        }
    }
}

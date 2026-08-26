using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sprint D Part 1 — `api_keys` มีอยู่ในสคีมาตั้งแต่ InitialSchema (2026-08-21) แต่ไม่เคยมี
    /// endpoint ต่อเข้าจริงมาก่อน สองช่องโหว่ที่เจอตอนต่อจริงครั้งแรก:
    ///  1. index (user_id, provider) เดิมไม่ unique — "หนึ่ง key ต่อหนึ่ง provider ต่อ user" ไม่ถูก
    ///     บังคับที่ DB เลย ถ้า client ยิง PUT ซ้อนกันสองครั้งพร้อมกันจะได้ 2 แถวสำหรับ provider เดียวกัน
    ///  2. `status` (ApiKeyStatus enum) ไม่มี CHECK constraint เหมือน enum column อื่นทุกตัวในระบบนี้
    ///     (ขัดกับหลักที่ล็อกไว้ใน db-schema.md: "enum เก็บเป็น text + มี CHECK constraint กำกับทุกตัว")
    /// ปลอดภัยที่จะแก้ตอนนี้เพราะยังไม่มีแถวจริงในตาราง (ยังไม่เคยมี endpoint ใช้งานเลย)
    /// </summary>
    public partial class AddApiKeysConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_api_keys_user_id_provider",
                table: "api_keys");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_user_id_provider",
                table: "api_keys",
                columns: new[] { "user_id", "provider" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_status_apikey",
                table: "api_keys",
                sql: "status IN ('Active', 'Invalid', 'Revoked')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_api_keys_user_id_provider",
                table: "api_keys");

            migrationBuilder.DropCheckConstraint(
                name: "ck_status_apikey",
                table: "api_keys");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_user_id_provider",
                table: "api_keys",
                columns: new[] { "user_id", "provider" });
        }
    }
}

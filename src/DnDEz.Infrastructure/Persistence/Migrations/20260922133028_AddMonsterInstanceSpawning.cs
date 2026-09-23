using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Part E ของแผน "DM AI Assistant" (2026-09-22) — ทำให้ **มอนสเตอร์มี HP รายตัวและเข้าวง initiative ได้**
    /// ซึ่งเป็นเรื่องที่ผู้ใช้ค้างไว้ตั้งแต่ต้นเซสชัน ("Monster ไม่มี hp รายตัว")
    ///
    /// ## 1. `characters.spawned_from_monster_id`
    /// มอนสเตอร์ในไฟต์ถูก spawn เป็นแถว `characters` (ControlledByDm=true) แทนการสร้างตารางใหม่ —
    /// เหตุผลเต็มอยู่ใน doc comment ของ property `Character.SpawnedFromMonsterId` โดยย่อคือ
    /// `EncounterParticipant` รับได้แค่ `CharacterId` การ spawn เป็น Character จึงได้ลำดับเทิร์น +
    /// การ resolve การโจมตี + broadcast `HpUpdated` + การซ่อน HP จากผู้เล่น มาใช้ได้ทั้งหมดโดยไม่ต้อง
    /// เขียนระบบคู่ขนาน
    ///
    /// ⚠️ **จงใจไม่ผูก FK ไปหา `monster_stat_blocks`** — คอลัมน์นี้มีหน้าที่หลักคือธง "แถวนี้เป็นของ
    /// ชั่วคราวประจำไฟต์ ลบได้ตอนจบ" ถ้าผูก FK แบบ `SetNull` แล้ววันหนึ่ง DM ลบแม่แบบทิ้งกลางไฟต์
    /// ธงจะถูกล้างเป็น null เงียบ ๆ → ตอนจบ encounter จะ**ลบไม่ออก** กลายเป็นขยะค้างในแคมเปญตลอดไป ·
    /// ส่วน `Cascade` ก็ผิดเช่นกัน (ลบแม่แบบไม่ควรลบตัวละครที่กำลังสู้อยู่กลางเทิร์น) · ค่าที่ชี้ไปหา
    /// แม่แบบที่ถูกลบไปแล้วยอมรับได้ เพราะใช้เป็นแค่ที่มาเชิงข้อมูล ไม่ได้ join บังคับ
    ///
    /// ## 2. `JournalAuthor` เพิ่มค่า `System`
    /// ตอน encounter จบ ระบบเขียนสรุปผลไฟต์ลงบันทึกแคมเปญ (Part D) ให้อัตโนมัติ — ข้อความนั้น
    /// **ไม่ใช่ทั้ง DM พิมพ์เองและไม่ใช่ AI แต่ง** แต่เป็นข้อเท็จจริงที่โค้ดสรุปจาก state จริง
    /// ถ้าติดป้ายเป็น `Ai` คนอ่านทีหลัง (หรือ AI เองตอนค้นบันทึก) จะเข้าใจผิดว่าข้อความนี้อาจถูกแต่งขึ้น
    /// จึงต้องมีค่าที่สามแยกออกมา — CHECK constraint ของ `authored_by` ถูก drop/recreate ตามไปด้วย
    /// </summary>
    public partial class AddMonsterInstanceSpawning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_authored_by_campaignjournalentry",
                table: "campaign_journal_entries");

            migrationBuilder.AddColumn<Guid>(
                name: "spawned_from_monster_id",
                table: "characters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_authored_by_campaignjournalentry",
                table: "campaign_journal_entries",
                sql: "authored_by IN ('Dm', 'Ai', 'System')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_authored_by_campaignjournalentry",
                table: "campaign_journal_entries");

            migrationBuilder.DropColumn(
                name: "spawned_from_monster_id",
                table: "characters");

            migrationBuilder.AddCheckConstraint(
                name: "ck_authored_by_campaignjournalentry",
                table: "campaign_journal_entries",
                sql: "authored_by IN ('Dm', 'Ai')");
        }
    }
}

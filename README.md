# DnDEz

แพลตฟอร์มเล่น D&D ที่มี **AI เป็นผู้ช่วยของ DM** — ปรับระดับการช่วยได้ตั้งแต่ทำแทนเกือบทั้งหมด
จนถึงเงียบสนิท รองรับทั้งเล่นออนไลน์ ล้อมวงเจอกัน ใช้เครื่อง DM เครื่องเดียว หรือ quick play

## เอกสาร

| ไฟล์ | ใช้เมื่อไหร่ |
|---|---|
| [`docs/PROJECT-CONTEXT.md`](./docs/PROJECT-CONTEXT.md) | **เริ่มที่นี่** — primer สำหรับเปิดแชทใหม่ / เริ่มงานใหม่ สรุปสิ่งที่ล็อกแล้วและสิ่งที่ค้าง |
| [`docs/dm-side-design.md`](./docs/dm-side-design.md) | รายละเอียดเต็มฝั่ง DM — data model, DM console, source import, BYOK |

## สถานะ

- ✅ ฝั่ง DM — ออกแบบเสร็จ
- ⬜ ฝั่ง Player — ยังไม่เริ่ม (ขั้นถัดไป)
- ⬜ โค้ด — ยังไม่เริ่ม

## Stack

.NET (ASP.NET Core) + SignalR · PostgreSQL + Redis · Next.js · Claude API · Docker + Jenkins

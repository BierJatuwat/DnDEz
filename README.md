# DnDEz

แพลตฟอร์มเล่น D&D ที่มี **AI เป็นผู้ช่วยของ DM** — ปรับระดับการช่วยได้ตั้งแต่ทำแทนเกือบทั้งหมด
จนถึงเงียบสนิท รองรับทั้งเล่นออนไลน์ ล้อมวงเจอกัน ใช้เครื่อง DM เครื่องเดียว หรือ quick play

## เอกสาร

| ไฟล์ | ใช้เมื่อไหร่ |
|---|---|
| [`docs/PROJECT-CONTEXT.md`](./docs/PROJECT-CONTEXT.md) | **เริ่มที่นี่** — primer สำหรับเปิดแชทใหม่ / เริ่มงานใหม่ สรุปสิ่งที่ล็อกแล้วและสิ่งที่ค้าง |
| [`docs/dm-side-design.md`](./docs/dm-side-design.md) | รายละเอียดเต็มฝั่ง DM — data model, DM console, source import, BYOK |
| [`docs/player-side-design.md`](./docs/player-side-design.md) | รายละเอียดเต็มฝั่งผู้เล่น — action input, fog of war, turn UI, คลัง preset, join flow |

## สถานะ

- ✅ ฝั่ง DM — ออกแบบเสร็จ
- ✅ ฝั่ง Player — ออกแบบรอบแรกเสร็จ
- ⬜ โค้ด — ยังไม่เริ่ม

## Stack

.NET (ASP.NET Core) + SignalR · PostgreSQL (JSONB + pgvector) + Redis · Hangfire ·
Next.js + react-konva (PWA) · Claude API · Docker + Jenkins

รายละเอียดเต็ม (แบ่งโมเดลตามงาน, ข้อบังคับทางสถาปัตยกรรม) อยู่ใน
[`docs/PROJECT-CONTEXT.md`](./docs/PROJECT-CONTEXT.md) §3

## Dev workflow — Tier Agent System

Repo นี้ติดตั้ง [Tier Agent System](./CLAUDE.md) ไว้แล้ว — แบ่งงานเป็น tier ตามความยาก
(haiku < sonnet < opus < fable) พร้อม code review / security review / TDD flow ที่กำหนดไว้

- **ปิดโดยดีฟอลต์** — พิมพ์คำสั่งปกติ = ทำงานตรงไปตรงมา ไม่มี routing
- **เปิด**: ขึ้นต้น prompt ด้วย `<tier>`
- ดูวิธีใช้เต็มด้วย `/tier-help` หรืออ่าน [`CLAUDE.md`](./CLAUDE.md)

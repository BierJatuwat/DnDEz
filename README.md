# DnDEz

แพลตฟอร์มเล่น D&D ที่มี **AI เป็นผู้ช่วยของ DM** — ปรับระดับการช่วยได้ตั้งแต่ทำแทนเกือบทั้งหมด
จนถึงเงียบสนิท รองรับทั้งเล่นออนไลน์ ล้อมวงเจอกัน ใช้เครื่อง DM เครื่องเดียว หรือ quick play

## เอกสาร

| ไฟล์ | ใช้เมื่อไหร่ |
|---|---|
| [`docs/PROJECT-CONTEXT.md`](./docs/PROJECT-CONTEXT.md) | **เริ่มที่นี่** — primer สำหรับเปิดแชทใหม่ / เริ่มงานใหม่ สรุปสิ่งที่ล็อกแล้วและสิ่งที่ค้าง |
| [`docs/dm-side-design.md`](./docs/dm-side-design.md) | รายละเอียดเต็มฝั่ง DM — data model, DM console, source import, BYOK |
| [`docs/player-side-design.md`](./docs/player-side-design.md) | รายละเอียดเต็มฝั่งผู้เล่น — action input, fog of war, turn UI, คลัง preset, join flow |
| [`docs/db-schema.md`](./docs/db-schema.md) | โครงสร้าง DB — ตาราง, constraint ที่บังคับกฎ, RLS และวิธีต่อ DB |

## สถานะ

- ✅ ฝั่ง DM — ออกแบบเสร็จ
- ✅ ฝั่ง Player — ออกแบบรอบแรกเสร็จ
- 🚧 โค้ด — DB schema + RLS เสร็จแล้ว (30 ตาราง) · rules engine Phase 1 (dice) เสร็จแล้ว · ยังไม่มี API/frontend

## เริ่มต้นใช้งาน (local dev)

```bash
docker compose up -d postgres redis minio
export DOTNET_ROOT="$HOME/.dotnet"                 # macOS: SDK 8 อยู่ที่ ~/.dotnet
dotnet ef database update --project src/DnDEz.Infrastructure
```

### หมายเหตุสภาพแวดล้อม (macOS เครื่องนี้)

- **ต้องตั้ง `DOTNET_ROOT="$HOME/.dotnet"` เสมอ** ไม่งั้น `dotnet-ef` จะไปเจอ dotnet ของระบบที่
  `/usr/local/share/dotnet` ซึ่งมีแค่ runtime 6/7 แล้วฟ้อง *"You must install or update .NET"*
- `dotnet-ef` 8.0.10 ติดตั้งเป็น global tool แล้ว (`~/.dotnet/tools`)
- **ไม่มี `psql` ในเครื่อง** — ใช้ผ่าน container แทน:
  `docker compose exec postgres psql -U dndez -d dndez`
- ทดสอบ RLS ต้องต่อด้วย role `dndez_app` ผ่าน TCP (ไม่ใช่ unix socket):
  `docker compose exec -e PGPASSWORD=dndez_app postgres psql -U dndez_app -d dndez -h 127.0.0.1`
- Docker Desktop ต้องเปิดอยู่ก่อน (`open -a Docker`)

โครงสร้างโค้ด:

| โฟลเดอร์ | เนื้อหา |
|---|---|
| `src/DnDEz.Domain` | entity + enum (ไม่มี dependency กับ EF/infra) |
| `src/DnDEz.Infrastructure` | DbContext, EF configuration, migrations |
| `src/DnDEz.Rules` | rules engine — zero-dependency (BCL ล้วน) ห้าม reference EF/infra/orchestrator |
| `tests/DnDEz.Rules.Tests` | xUnit ของ rules engine |

รันเทสต์ของ rules engine:

```bash
DOTNET_ROOT="$HOME/.dotnet" dotnet test tests/DnDEz.Rules.Tests/DnDEz.Rules.Tests.csproj
```

**สำคัญ**: แอปต้องต่อ DB ด้วย role `dndez_app` (ไม่ใช่ owner) และตั้ง `app.user_id` / `app.campaign_id`
ทุก request ไม่งั้น RLS จะมองไม่เห็นข้อมูลใด ๆ เลย (fail closed) — ดู `docs/db-schema.md`

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

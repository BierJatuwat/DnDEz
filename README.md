# DnDEz

แพลตฟอร์มเล่น D&D ที่มี **AI เป็นผู้ช่วยของ DM** — ปรับระดับการช่วยได้ตั้งแต่ทำแทนเกือบทั้งหมด
จนถึงเงียบสนิท รองรับทั้งเล่นออนไลน์ ล้อมวงเจอกัน ใช้เครื่อง DM เครื่องเดียว หรือ quick play

## เอกสาร

| ไฟล์ | ใช้เมื่อไหร่ |
|---|---|
| [`docs/PROJECT-CONTEXT.md`](./docs/PROJECT-CONTEXT.md) | **เริ่มที่นี่** — การตัดสินใจที่ล็อกแล้ว (§2), stack (§3), ประวัติ sprint และบทเรียน |
| [`docs/BACKLOG.md`](./docs/BACKLOG.md) | **งานค้าง** — สิ่งที่ยังไม่ได้ทำ/รอตัดสินใจ/ยังไม่เคยทดสอบกับของจริง |
| [`CONTRIBUTING.md`](./CONTRIBUTING.md) | **วิธีทำงานร่วมกัน** — branch/PR, แบบแผนโค้ด, กฎ migration/RLS, เทสต์, checklist ความปลอดภัย |
| [`docs/dm-side-design.md`](./docs/dm-side-design.md) | ออกแบบฝั่ง DM — data model, DM console, source import, BYOK |
| [`docs/player-side-design.md`](./docs/player-side-design.md) | ออกแบบฝั่งผู้เล่น — action input, fog of war, turn UI, คลัง preset, join flow |
| [`docs/spatial-play-design.md`](./docs/spatial-play-design.md) | แผนที่/การต่อสู้/การสร้างตัวละคร — **แก้/ขยายสองเล่มบนหลายจุด** อ่านด้วยถ้าแตะส่วนนี้ |
| [`docs/db-schema.md`](./docs/db-schema.md) | โครงสร้าง DB — ตาราง, constraint ที่บังคับกฎ, RLS |

## สถานะ (2026-11-05)

ใช้งานได้จริงแล้ว: บัญชี/แคมเปญ/เข้าร่วมโต๊ะ · rules engine (ทอยเต๋า, โจมตี, ดาเมจ, death save, action economy) ·
realtime (SignalR) · แผนที่ + กริด + token + หมอก + พื้นเดินยาก · encounter/initiative · คลัง SRD 5.2.1 (อังกฤษ/ไทย) ·
**ผู้ช่วย DM ช่องเดียว** (ถามข้อมูล + สั่งงานหลายขั้น ผ่าน autonomy dial) · เนื้อเรื่อง (story beat) + นำเข้าจาก PDF/ภาพ

ทำครบถึง Sprint H Part 7 — รายละเอียดแต่ละ sprint ใน `PROJECT-CONTEXT.md` §4 · งานที่เหลือดู [`docs/BACKLOG.md`](./docs/BACKLOG.md)

## เริ่มต้นใช้งาน (local dev)

**ต้องมี**: Docker Desktop · .NET SDK 8 ขึ้นไป **และ** .NET 8 runtime (โปรเจกต์ target `net8.0`) ·
Node.js 20.9 ขึ้นไป · `dotnet-ef` 8.x (`dotnet tool install --global dotnet-ef --version 8.0.10`)

```bash
# 1. ฐานข้อมูล / cache / ที่เก็บไฟล์
docker compose up -d postgres redis minio

# 2. สร้างตาราง (ไม่รันอัตโนมัติตอนเปิด API — ต้องรันเองทุกครั้งที่มี migration ใหม่)
dotnet ef database update --project src/DnDEz.Infrastructure

# 3. API — ต้องเป็น Development ถึงจะมี JWT signing key สำรองสำหรับ dev
#    (bash/zsh)        ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DnDEz.Api --urls http://localhost:5080
#    (PowerShell)      $env:ASPNETCORE_ENVIRONMENT="Development"; dotnet run --project src/DnDEz.Api --urls http://localhost:5080
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DnDEz.Api --urls http://localhost:5080

# 4. Frontend (terminal แยก)
cd frontend && cp .env.example .env.local && npm install && npm run dev   # http://localhost:3001
```

- bucket ใน MinIO และ role `dndez_app` ของฐานข้อมูลถูกสร้างให้อัตโนมัติ (ตอนเปิด API / ใน migration)
- **พอร์ต frontend เป็น `3001`** ไม่ใช่ 3000 — backend เปิด CORS เฉพาะ origin ใน `DNDEZ_FRONTEND_ORIGIN`
  (ค่าเริ่มต้น `http://localhost:3001`) ถ้าเปลี่ยนพอร์ตต้องตั้ง env var นี้ให้ตรง
- **ฟีเจอร์ AI ใช้ Anthropic API key ของแต่ละคนเอง (BYOK)** — สมัครบัญชี DM แล้วใส่ key ในหน้า DM console
  ไม่มี key กลางของโปรเจกต์ และไม่ต้องใส่ key ในไฟล์ใด ๆ
- ใช้ psql ผ่าน container: `docker compose exec postgres psql -U dndez -d dndez`
- macOS ที่มี .NET หลายชุด: ถ้า `dotnet-ef` ฟ้อง *"You must install or update .NET"* ให้ตั้ง `DOTNET_ROOT` ชี้ SDK 8

## รันเทสต์

```bash
# rules engine — ไม่ต้องมี Docker
dotnet test tests/DnDEz.Rules.Tests

# API — integration test ต่อ Postgres/Redis/MinIO จริง (ไม่ mock) ต้อง docker compose up + migrate ก่อน
dotnet test tests/DnDEz.Api.Tests

# frontend
cd frontend && npx vitest run && npx tsc --noEmit && npm run lint
```

เทสต์ทุกตัวใช้ `FakeAnthropicClient` แทน Claude จริง (ไม่มีค่าใช้จ่าย ไม่ต้องมี key) · CI
(`.github/workflows/ci.yml`) รันชุดเดียวกันนี้ทุก PR และทุก push เข้า `main` · วิธีทำงานร่วมกันดู
[`CONTRIBUTING.md`](./CONTRIBUTING.md)

ถ้ารัน API ค้างไว้ระหว่างรันเทสต์บน Windows อาจเจอ `file is locked by DnDEz.Api` — ปิด API ก่อน หรือรันเทสต์ด้วย `-c Release`

## โครงสร้างโค้ด

| โฟลเดอร์ | เนื้อหา |
|---|---|
| `src/DnDEz.Domain` | entity + enum (ไม่มี dependency กับ EF/infra) |
| `src/DnDEz.Infrastructure` | DbContext, EF configuration, migrations, ข้อมูล SRD ที่ seed |
| `src/DnDEz.Rules` | rules engine — zero-dependency (BCL ล้วน) ห้าม reference EF/infra/orchestrator |
| `src/DnDEz.Api` | Minimal API + SignalR hub + auth · `Assistant/` = ผู้ช่วย DM · `Orchestration/` = client ของ Claude |
| `frontend/` | Next.js 16 (App Router) + PWA — ⚠️ เวอร์ชันนี้ API ต่างจากที่คุ้นเคย อ่าน `frontend/AGENTS.md` ก่อน |
| `tests/DnDEz.Rules.Tests` | xUnit ของ rules engine |
| `tests/DnDEz.Api.Tests` | integration test ของ API ผ่าน `WebApplicationFactory` |
| `tools/` | สคริปต์แปลง/นำเข้า SRD |

## กฎที่ห้ามพลาด

รายละเอียดเต็มอยู่ใน `PROJECT-CONTEXT.md` §2–§3 และ `CLAUDE.md` — ข้อที่เคยพลาดจริงมาแล้ว:

- **ตารางใหม่ทุกตัวต้องเปิด RLS เองใน migration นั้น** (`ENABLE` + `FORCE` + `CREATE POLICY`) — ไม่มีการสืบทอดจาก
  migration เดิม ลืมแล้ว **fail open** (ทุกคนอ่าน/เขียนได้) ไม่ใช่ fail closed
- แอปต่อ DB ด้วย role `dndez_app` และตั้ง `app.user_id`/`app.campaign_id` ทุก request (ทำให้แล้วใน `RlsRequestTransaction`)
- **ความลับของ DM** (`DmNotes`, `Secrets`, บทสนทนากับผู้ช่วย DM) ห้ามเข้า prompt ของเทิร์นผู้เล่น (`OrchestratorTurnRunner`)
  เพราะคำตอบถูก broadcast ถึงผู้เล่น — รั่วแบบนี้มาแล้ว 4 ครั้ง
- ข้อมูลที่ AI แยกจากเอกสาร (`ReviewStatus.Pending`) ห้ามเข้า prompt ของ AI จนกว่า DM จะยืนยัน
- เทสต์ต่อฐานข้อมูลจริง ห้าม mock · แก้เรื่อง security ต้องมีเทสต์ที่ **ล้มก่อนแก้** เพื่อพิสูจน์ว่าช่องโหว่มีจริง

## Stack

.NET 8 (ASP.NET Core Minimal API) + SignalR · PostgreSQL 16 (JSONB + pgvector + RLS) · Redis · MinIO ·
Next.js 16 + TanStack Query + Zustand (PWA) · Claude API (BYOK)

## Claude Code (ไม่บังคับ)

`CLAUDE.md` และ `.claude/` เป็นการตั้งค่าสำหรับคนที่ใช้ [Claude Code](https://claude.com/claude-code) — มี
Tier Agent System (เปิดด้วยการขึ้นต้น prompt ด้วย `<tier>`, ดู `/tier-help`) · ใครไม่ได้ใช้ Claude Code ข้ามส่วนนี้ได้
แต่บทเรียนท้าย `CLAUDE.md` ("บทเรียนทางเทคนิคเฉพาะโปรเจกต์นี้") ควรอ่านทุกคน

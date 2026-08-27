# DnDEz frontend

Next.js (App Router) + PWA — ดู [`../README.md`](../README.md) และ
[`../docs/PROJECT-CONTEXT.md`](../docs/PROJECT-CONTEXT.md) สำหรับบริบทเต็มของโปรเจกต์

## เริ่มต้นใช้งาน

ต้องมี API รันอยู่ที่ `http://localhost:5080` ก่อน (ดูวิธีรันใน `../README.md`)

```bash
cp .env.example .env.local   # ปรับ NEXT_PUBLIC_API_BASE_URL ถ้า API รันคนละพอร์ต
npm install
npm run dev                  # http://localhost:3001
```

พอร์ต `3001` ต้องตรงกับ `DNDEZ_FRONTEND_ORIGIN` ที่ API ตั้ง CORS ไว้ (default `http://localhost:3001`)

## Stack

- **TanStack Query** — server state (campaigns/sessions/mutations)
- **Zustand** — UI state (`src/lib/console-store.ts`) + auth token persisted ผ่าน
  `zustand/middleware persist` (`src/lib/auth-store.ts`)
- **@microsoft/signalr** — realtime connection ไปหา `CampaignHub` (`src/lib/signalr.ts`)

ดูรายละเอียดการตัดสินใจสถาปัตยกรรม (ตำแหน่ง frontend ใน repo, CORS, JWT storage) ใน
`../docs/PROJECT-CONTEXT.md` §4 "Sprint E"

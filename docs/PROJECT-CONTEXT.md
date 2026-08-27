# DnDEz — Project context (handoff)

> เอกสารนี้คือ **primer สำหรับเริ่มแชทใหม่** (Cowork, Claude Code, หรือแชทออกแบบต่อ)
> อ่านไฟล์นี้ก่อน แล้วค่อยเปิด [`dm-side-design.md`](./dm-side-design.md) /
> [`player-side-design.md`](./player-side-design.md) ถ้าต้องการรายละเอียดเต็ม
>
> **สถานะ**: ฝั่ง DM ออกแบบเสร็จแล้ว · ฝั่ง Player ออกแบบรอบแรกเสร็จแล้ว (2026-08-21)
> · **DB schema + RLS ลงโค้ดแล้ว** (30 ตาราง — ดู [`db-schema.md`](./db-schema.md)) ·
> **rules engine ครบ 3 phase แล้ว** (221 เทสต์ผ่าน — ดู §4) · ยังไม่มี API/frontend
>
> **ก่อนเริ่มเขียนโค้ด**: repo นี้มีระบบ [Tier Agent System](../CLAUDE.md) ติดตั้งไว้แล้ว (routing/review/TDD
> convention สำหรับ Claude Code) — ปิดโดยดีฟอลต์ เปิดด้วยการขึ้นต้น prompt ด้วย `<tier>` เท่านั้น
> ไม่บังคับต้องใช้ทุกครั้ง แต่ควรรู้จักไว้ก่อนเริ่มงานเขียนโค้ดจริง ดู `/tier-help`

---

## 1. โปรเจกต์นี้คืออะไร

แพลตฟอร์มเล่น D&D ที่มี **AI เป็นผู้ช่วยของ DM** (ไม่ใช่แทนที่ DM)
ปรับระดับการช่วยได้ตั้งแต่ทำแทนเกือบทั้งหมด จนถึงเงียบสนิท

เป้าหมาย: ทำให้คนที่ไม่เคยเป็น DM กล้าเปิดโต๊ะแรก + ทำให้ DM ที่เก่งอยู่แล้วทำงานน้อยลง

**ไม่ใช่**: VTT 3D แบบ Foundry/Roll20 · rules engine ครบทุก sourcebook · social platform หาเพื่อนเล่น

### 4 โหมดการเล่นที่ต้องรองรับทุกฟีเจอร์
| โหมด | อธิบายสั้น |
|---|---|
| Online | ทุกคนคนละที่ คนละเครื่อง |
| In-person | ล้อมวงจริง แต่ละคนมีแท็บเล็ต + จอกลาง (table view) |
| Companion | มีแค่เครื่อง DM เครื่องเดียว ผู้เล่นใช้กระดาษ note แต่ระบบยัง track resource ให้ |
| Quick play | ผู้เล่นใส่แค่ชื่อ ไม่ต้องสมัครบัญชี |

---

## 2. การตัดสินใจที่ล็อกแล้ว (ห้ามเปลี่ยนโดยไม่คุยกันก่อน)

### สถาปัตยกรรม
- **แยก AI orchestrator ออกจาก rules engine เด็ดขาด** — AI ตัดสินใจว่าจะเรียก tool ไหน
  (tool calling) แต่ **rules engine ที่เป็นโค้ดปกติ** เป็นคนคำนวณและเขียน state จริง
  → ตัวเลขถูก 100%, กัน prompt injection, ลด context
- **ลำดับการ build: rules engine ก่อน API layer** (ยืนยัน 2026-08-21 · Phase 1 ลงโค้ดแล้ว)
  `DnDEz.Rules` เป็น class library **zero-dependency (BCL ล้วน ไม่มี PackageReference เลย)**
  ห้าม reference EF Core / Infrastructure / orchestrator · **ไม่เขียน DB เอง** คืน record ล้วน
  การ persist เป็นหน้าที่ชั้น application · ฉีด `IDiceRoller` เข้าไปให้ deterministic ทั้งก้อน ·
  **engine ไม่รู้จัก concept ผู้ใช้/สิทธิ์เลย** (กฎ "ผู้เล่นกรอกผลเต๋าเองไม่ได้" บังคับที่ชั้น authorization ไม่ใช่ที่ engine)
- **Approval gate** — tool call ใน subsystem ที่ตั้งเป็น "suggest" จะกลายเป็น pending proposal
  ส่งเข้า DM เท่านั้น ผู้เล่นไม่เห็นจนกว่า DM จะ approve
- **AI autonomy เป็น dial ต่อ subsystem** (narration / NPC dialogue / rules-combat / map-scene)
  แต่ละอันตั้งได้เป็น manual / suggest / autonomous ปรับได้ระหว่างเกม
- **กฎตายตัวที่ dial เปลี่ยนไม่ได้**: AI ห้ามสั่ง action แทนตัวละครผู้เล่นโดยไม่ผ่านการยืนยัน
  จากผู้เล่นเจ้าของตัวละคร
- **Guided vs Custom mode** ใช้ schema เดียวกัน ต่างแค่ค่าตั้งต้น + ซ่อน/แสดง UI
  สลับไปมาได้โดยข้อมูลไม่หาย (ห้ามทำเป็นสองระบบแยก)
- **ต้องมีอินเทอร์เน็ตเสมอ** — ไม่ทำ offline mode

### ธุรกิจ / บัญชี
- **BYOK**: DM ใช้ Anthropic API key ของตัวเอง เก็บแบบเข้ารหัส (ASP.NET Data Protection API)
  **ผูกกับ user ไม่ใช่ campaign** (เผื่ออนาคตผู้เล่นใช้ key ตัวเอง)
- **DM ต้องมีบัญชีเต็มเสมอ** · ผู้เล่นเลือก quick play ได้
- Quick play: จบ session ถามว่าจะเก็บตัวละครไหม → ไม่ตอบ = เก็บเป็น orphan + claim link 7 วัน → hard delete
  - **(แก้ 2026-08-21)** เพิ่มตัวเลือก consent: "ไม่เก็บตัวละคร แต่อนุญาตให้นำ**โครงสร้างการสร้าง**
    ไปใช้เป็นแม่แบบ" → เข้าคลัง preset (ตัดชื่อ/backstory/รูป/โน้ตทิ้ง เหลือแค่ build)
    **ถ้าไม่ติ๊ก = hard delete ตามเดิม** · รายละเอียดกติกา harvest ใน `player-side-design.md` §19
- Quick-play players ใช้ key ของ DM เท่านั้น
- Storage quota **per account** (แชร์ทั้งบัญชีไม่ว่าเล่นเป็น DM หรือ player) เผื่อขายเพิ่มทีหลัง

### ขอบเขตกฎ
- ฐานคือ **D&D 2024 / SRD 5.2** (SRD 5.2 เป็น CC-BY-4.0 → ฝังเป็น RulesCorpus ในระบบได้เลย)
- ต่อยอด homebrew บนฐานนี้ได้
- Rest ยึดรูปแบบ D&D Beyond: **DM เป็นคนกดเปิด** short/long rest → ผู้เล่นเลือก hit dice /
  สกิลช่วงพักบนเครื่องตัวเอง → ระบบ resolve รวดเดียว (long rest กรอกชั่วโมง + ลำดับเฝ้าเวรได้)
- Recap: **มีสองเวอร์ชันเสมอ** (DM version รวม secret / Player version เฉพาะที่ตัวละครรู้)
  ถามตอน resume ว่าจะ recap ไหม ไม่ auto
- **Discord = แค่ช่องเสียง** ไม่ทำ bot / embedded app

### Source import (สแกนหนังสือ)
- แปลงเป็น structured data ก่อนใช้เสมอ (ไม่โยน PDF ดิบให้ AI)
- **ห้าม commit อัตโนมัติ** ต้องผ่านหน้า DM review (extraction ผิดพลาดได้ ถ้า HP ผิดเกมพัง)
- record ที่ `review_status != approved` ห้ามส่งเข้า orchestrator context
- Private ต่อ campaign เสมอ, vector index แยก namespace, ปิดได้ด้วย feature flag `ENABLE_SOURCE_IMPORT`
- **ห้ามเนื้อหาจาก source import หลุดออกนอกแคมเปญผ่านทางอ้อม** — ตัวละครที่ใช้ไอเทม/feat จาก
  `SourceDocument` แม้ชิ้นเดียว harvest เข้าคลัง preset กลางไม่ได้ (ไปได้แค่คลังส่วนตัวของ DM คนนั้น)

### ฝั่งผู้เล่น (ล็อก 2026-08-21 — รายละเอียดเต็มใน `player-side-design.md`)
- Player client ใช้ **game state เดียวกับ DM console** ต่างแค่มุมมอง/สิทธิ์ —
  action ทุกอันไหลผ่าน rules engine + approval gate เดิม ห้ามมีเส้นทางแก้ state คู่ขนาน
- **Action input เป็น hybrid**: ปุ่มคำสั่งพื้นฐาน + free text สำหรับ role-play
- **Fog of war คำนวณอัตโนมัติ** จาก position + ความสามารถของตัวละคร (นับของเพื่อนร่วมทีมด้วย)
  อัปเดต real-time แม้ไม่ใช่เทิร์นตัวเอง
- **Reaction ใช้ timeout** DM ตั้งเวลาได้ ดีฟอลต์ 30 วินาที หมดเวลา = ไม่ใช้
- **เต๋ามี 2 โหมด**: ระบบทอยให้ / DM กรอกผลเต๋าจริง — **ผู้เล่นกรอกผลเองไม่ได้**
- **DM เข้าควบคุมตัวละครผู้เล่นได้ตลอดเวลา** (ไม่ใช่แค่ตอนหลุด) ต้องมี log ว่า action มาจาก DM ·
  **AI ยังห้ามเด็ดขาดตามกฎเดิม**
- **ผู้เล่นไม่เห็นคิว proposal** ไม่มีแม้แต่สถานะ "DM กำลังพิจารณา"
- **rules engine เป็นเจ้าของตัวเลขกลไก** ผู้เล่นแก้ HP/resource/XP เองไม่ได้ ต้องขอผ่าน DM
- **Panel แนะนำ action** จำกัดเฉพาะคนมี API token ของตัวเอง/DM แชร์ให้ และ**ขึ้นบนจอ DM**
  ให้ DM เป็นคนพูดแนะนำ (คุมต้นทุน token)
- **Condition tooltip ดึงจาก RulesCorpus ไม่เรียก AI** (ไม่เปลือง token)

---

## 3. Stack

> ทบทวนและขยายรายละเอียด 2026-08-21 — แกนหลักไม่เปลี่ยน เพิ่มส่วนที่ยังไม่เคยระบุ

| ส่วน | เลือกใช้ |
|---|---|
| Backend | .NET (ASP.NET Core) + **SignalR** (realtime hub เดียวรองรับทุกโหมด) |
| ORM / migration | **EF Core + Npgsql** (map JSONB ตรง ๆ) |
| DB | **PostgreSQL** (JSONB) + **pgvector** (RawChunk — ไม่เพิ่ม vector DB แยก) + **Redis** (presence, turn state, timer) |
| Background job | **Hangfire** (เก็บ state ใน Postgres ไม่เพิ่ม infra) — claim link 7 วัน, hard delete orphan, harvest, source import |
| File storage | **S3-compatible** (MinIO ถ้า self-host / R2 ถ้า cloud) + signed URL อายุสั้น |
| Frontend | **Next.js** + **react-konva** (grid map: token ลากได้, fog layer, hit detection) |
| Frontend state | `@microsoft/signalr` + TanStack Query (server state) + Zustand (UI state) |
| อุปกรณ์ผู้เล่น | **PWA** (แท็บเล็ต/มือถือ) — ไม่ทำ native app |
| Rules engine | **class library แยกโปรเจกต์** ห้าม reference orchestrator + xUnit ครอบหนัก |
| LLM | **Claude API** (official C# SDK รองรับ tool use) — ดูตารางแบ่งโมเดลด้านล่าง |
| Deploy | Docker + Jenkins |

### แบ่งโมเดลตามงาน (สำคัญเพราะ BYOK — ผู้ใช้จ่ายเอง)

| งาน | โมเดล |
|---|---|
| narration, NPC, tool call ทั่วไป | **Claude Sonnet 5** (จุดคุ้มสุด) |
| งานหนักครั้งเดียว เช่น `generate_campaign_skeleton` | **Claude Opus 5** |
| งานถูก เช่นสรุป log, จัดหมวด | **Claude Haiku 4.5** |

**ฟีเจอร์ Claude API ที่ต้องใช้ให้เต็มที่**
- **Structured outputs + strict tools** — บังคับ schema ตอน extraction จากหนังสือ ลดโอกาส AC/HP เพี้ยน
- **Batch API (ลด 50%)** — สแกนหนังสือทั้งเล่มไม่ต้อง real-time ยิงเป็น batch ประหยัดเงินผู้ใช้ครึ่งหนึ่ง
- **Files API** — อัปโหลด PDF ตรง ๆ ใช้ซ้ำได้หลาย request
- **Prompt caching** — แต่ต้องวางของที่เปลี่ยนทุกเทิร์นไว้**ท้าย** breakpoint ไม่งั้น cache แตกเงียบ ๆ
- **Token counting** — โชว์ผู้ใช้ว่าเซสชันนี้ใช้เงินไปเท่าไหร่

### ข้อบังคับทางสถาปัตยกรรม (ห้ามพลาด)

- **Fog of war คำนวณฝั่ง server และส่งเฉพาะส่วนที่มองเห็นจริง** — ห้ามส่งแผนที่เต็มแล้วบังด้วย CSS
  เพราะผู้เล่นเปิด devtools อ่าน websocket เห็นหมด = โกงได้ทันที
- **Reaction timeout เป็น timer ฝั่ง server** ไม่ใช่ `setTimeout` ฝั่ง client
- **RNG ทอยเต๋าอยู่ฝั่ง server + เก็บ seed ลง log** เพื่อ replay ตรวจสอบย้อนหลังเวลามีข้อพิพาท
  - ⚠️ **ต้อง generate seed ใหม่จาก CSPRNG ทุกครั้งที่ทอย** (`SeededDiceRoller.CreateWithSecureRandomSeed()`)
    **ห้ามใช้ roller ตัวเดียวยาวข้ามหลายการทอย** — PRNG ที่ใช้ (SplitMix64) ไม่ใช่ CSPRNG และ
    output function **ย้อนกลับหา state ได้** (ทดสอบยืนยันด้วยการเขียน inverse จริงแล้ว 2026-08-21)
    ผู้เล่นเห็นผลทอย d20 ได้ ~4.32 บิต/ครั้ง ครบ 64 บิตในราว 15 ครั้ง = พอทางทฤษฎีที่จะ pin state
    แล้วทำนายทอยถัดไป · การ re-seed ต่อการทอยทำให้ผลเก่าไม่บอกอะไรเกี่ยวกับผลถัดไป
    (schema เก็บ seed ต่อแถวใน `dice_rolls` รองรับรูปแบบนี้อยู่แล้ว)
  - **ห้ามแสดง seed ให้ผู้เล่นเห็น** — seed มีไว้ให้ DM/audit เท่านั้น
- **Postgres Row-Level Security** บังคับ campaign isolation ที่ชั้น DB อีกชั้น ไม่พึ่ง `WHERE` ในโค้ดอย่างเดียว
  - ⚠️ **policy ต้องเช็คว่า user เป็นสมาชิกแคมเปญจริง ไม่ใช่แค่ `campaign_id` ตรงกับค่าที่แอปส่งมา** —
    เจอตอนทดสอบจริงว่าถ้าเช็คแค่ค่าที่ตั้ง DM ของแคมเปญอื่นตั้งค่าเองแล้วอ่านข้ามได้ทันที (ดู `db-schema.md`)
- **แอปต้องต่อ DB ด้วย role ที่ไม่ใช่ owner** (`dndez_app`) — owner bypass RLS เสมอ

---

## 4. สิ่งที่ยังไม่ตัดสินใจ / ยังไม่ออกแบบ

### 🔜 ลำดับงานถัดไป — เลือก rules engine ก่อน API layer (ยืนยันแล้ว 2026-08-21)

ทางเลือกที่ค้างไว้: **(ก)** rules engine เป็น class library แยก + xUnit · **(ข)** ASP.NET Core API + SignalR hub
→ **เลือก (ก)** ด้วยเหตุผล:

1. **ทิศทางการพึ่งพาเป็นทางเดียว** — API / SignalR / orchestrator พึ่ง rules engine ทั้งหมด
   แต่ rules engine ไม่พึ่งใครเลย ทำก่อนไม่มีอะไรบล็อก ส่วนทำ API ก่อนจะได้แค่ท่อ CRUD เปล่า
   แล้วต้องรื้อ contract รอบสองตอน engine มา
2. **API contract ตกผลึกเองจาก result type ของ engine** — `RollResult` / `AttackResult` /
   `DamageResult` / `TurnBudget` เป็น payload ของ REST response, SignalR event และ tool-call schema
   ของ AI พร้อมกัน มี engine ก่อน = เขียน DTO ได้โดยไม่ต้องเดา
3. **ทดสอบจริงได้คุ้มที่สุด** — pure library รันเทสต์ deterministic ได้โดยไม่ต้อง spin Docker/websocket
   ถ้าทำ API ก่อน เทสต์ที่เขียนได้จะมีแค่ "endpoint คืน 200" ซึ่งไม่ยืนยันอะไร
4. **ความเสี่ยงกระจุกที่ engine ไม่ใช่ API** — advantage/disadvantage ซ้อน, resistance + vulnerability
   พร้อมกัน, temp HP, death save edge case (nat20 ฟื้น 1 HP / nat1 นับสอง / โดนตีตอนสลบ crit = 2),
   hit dice ตอน rest โดน encounter แทรก — จุดที่บั๊กเงียบและ "build ผ่าน" บอกอะไรไม่ได้
   ส่วน ASP.NET Core + SignalR เป็นงานมีแบบแผนชัด ความไม่แน่นอนต่ำ เลื่อนไปทีหลังไม่เพิ่มความเสี่ยง
5. **RNG + seed ต้องอยู่ใน engine ตั้งแต่บรรทัดแรก** — `dice_rolls.seed` มีคอลัมน์รออยู่แล้ว
   ถ้า retrofit `IDiceRoller` ที่ replay ได้ทีหลัง ต้องแก้ทุกจุดที่เรียกใช้

**ข้อจำกัดที่ต้องล็อกตั้งแต่ต้น** (ไม่งั้นเสียประโยชน์ทั้ง 5 ข้อข้างบน)
- `DnDEz.Rules` **ห้าม reference EF Core / Infrastructure / orchestrator** — รับ input เป็น snapshot record
  คืน result record **ไม่เขียน DB เอง** การ persist เป็นหน้าที่ของชั้น application
- ฉีด `IDiceRoller` / `IClock` เข้าไป ให้ engine เป็น deterministic ทั้งก้อน
- engine ไม่รู้จัก concept ผู้ใช้/สิทธิ์เลย — authorization อยู่ชั้นบนเท่านั้น

**Slice ที่เสนอ** (ไม่ทำ SRD ทั้งเล่มรวดเดียว)
S1 dice notation + advantage + seeded replay + ทางรับเลขดิบจาก DM ·
S2 check/save/attack vs AC · S3 damage & HP (resist/vuln/immune ซ้อน, temp HP, 0 HP) ·
S4 death save state machine · S5 action economy · S6 rest resolution
→ เลื่อน spellcasting เต็มรูปกับ fog of war ไว้ (fog ต้องรอ grid/geometry layer ก่อน)

**⚠️ จุดที่ต้องระวังตอนถึงคิว API** — งานที่ทดสอบได้เฉพาะชั้นนั้นคือ integration test ยืนยันว่า
EF Core ต่อด้วย role `dndez_app` + `SET LOCAL` แล้ว RLS ยังกันข้ามแคมเปญได้จริง
เพราะ **`SET LOCAL` ผูกกับ transaction** ถ้าเผลอเรียกนอก transaction บน connection pool
ค่าจะหลุดหรือค้างข้าม request — ช่องโหว่แบบเดียวกับที่เจอรอบก่อน แค่ย้ายมาอยู่ชั้นแอป

#### สถานะการลงโค้ด rules engine (แบ่ง 3 phase หยุดถามหลังจบทุก phase)

| Phase | ขอบเขต | สถานะ |
|---|---|---|
| **1** | โครงโปรเจกต์ + dice notation parser + advantage/disadvantage + seeded replay + ทาง DmEntered | ✅ **เสร็จ 2026-08-21** — 106 เทสต์ผ่านจริง (`dotnet test` 106/106) |
| **2** | check/save vs DC · attack vs AC · crit · damage + resist/vuln/immune · temp HP · 0 HP | ✅ **เสร็จ 2026-08-22** — 170 เทสต์ผ่านจริง (`dotnet test` 170/170) |
| **3** | death save state machine · action economy (action/bonus/reaction/movement) | ✅ **เสร็จ 2026-08-22** — 221 เทสต์ผ่านจริง (`dotnet test` 221/221) — **rules engine 3 phase ครบแล้ว** |

โครงสร้างที่ได้จาก Phase 1:
- `src/DnDEz.Rules/` — `DiceNotation.cs` (record/enum), `IDiceRoller.cs`, `SeededDiceRoller.cs`
  (SplitMix64 + CSPRNG seed factory), `DiceExpressionParser.cs`, `DiceEngine.cs`, `AdvantageResolver.cs`
- `tests/DnDEz.Rules.Tests/` — xUnit

โครงสร้างที่เพิ่มจาก Phase 2 (ต่อยอด `DiceEngine`/`DiceExpressionParser` เดิม ไม่เขียน d20-roller ใหม่):
- `src/DnDEz.Rules/CheckResolver.cs` — ability check/saving throw vs DC (`Total>=Dc` เท่านั้น
  **ไม่มี** auto-success/fail จาก nat20/nat1 — ต่างจาก attack roll โดยเจตนา)
- `src/DnDEz.Rules/AttackResolver.cs` — attack roll vs AC นับจาก **เลขดิบบน d20 ที่ถูกเก็บจริง**
  (ดู `Sides==20 && Dropped==false` ใน `RollResult.Rolls`) ไม่ใช่ total — nat20=CriticalHit เสมอ,
  nat1=AutomaticMiss เสมอไม่สนใจ AC/modifier · throw ถ้าพบ d20 ที่ไม่ถูก drop ไม่เท่ากับ 1 ตัวพอดี
  (ทั้งกรณีไม่มีเลยและกรณีเกิน 1 ตัว — กันกรณี caller ส่ง expression ที่ไม่ใช่ attack roll จริงเข้ามา)
- `DiceEngine.RollDamage(expression, roller, isCritical, seed)` — crit ดับเบิล **จำนวนลูกเต๋า**
  ต่อกลุ่ม (`DiceGroupSpec.Count`) เท่านั้น ไม่แตะ `FlatModifier` และไม่ดับเบิล `Keep.Amount` (kh/kl)
- `src/DnDEz.Rules/DamageType.cs` — 13 damage type มาตรฐาน SRD 5.2
- `src/DnDEz.Rules/DamageTypeResolver.cs` — resist/vulnerable/immune ต่อ damage type เดียว
  (input เป็น `IReadOnlySet<DamageType>` กัน stack ซ้อน) — **immune ชนะทุกกรณี** · **resist+vulnerable
  พร้อมกัน = หักล้างเป็น damage ปกติ** (implement เป็นนิยามตรง ๆ ไม่ใช่ `floor(x/2)*2` ซึ่งได้ผลต่างกัน
  จริง เช่น raw=7 → นิยามตรงได้ 7 แต่ floor-then-double ได้ 6) · resist อย่างเดียว = `/2` (floor)
  · vulnerable อย่างเดียว = `×2`
- `src/DnDEz.Rules/DamageResolver.cs` — ทอย damage หลาย component คนละ type ในการโจมตีเดียวได้
  (`DamageComponent(Expression, Type)`) · **ค่าที่ส่งเข้า resistance math ถูก clamp `Math.Max(0, ...)`
  เสมอ** (กัน damage ติดลบจาก expression แบบ `"1d4-6"`) แต่ `DamageComponentResult.RawAmount`
  ยังเก็บค่าดิบจริงไว้เพื่อ audit — ตั้งใจให้ต่างจาก `AdjustedAmount`
- `src/DnDEz.Rules/HitPointResolver.cs` — apply damage (temp HP ดูดซับก่อน HP จริงเสมอ, HP clamp
  ที่ 0, massive damage instant death: `excess=damageAppliedToHp-previousHp; IsInstantDeath =
  droppedToZero && excess>=maxHp`) + healing (`min(current+amount,max)`) — **Phase 2 แค่รายงาน
  `IsInstantDeath`/`DroppedToZero` เป็น fact ไม่จัดการ state ตาย/สลบ/death save เอง** นั่นเป็นหน้าที่
  Phase 3 · **Temp HP ไม่ stack — ใช้ `Math.Max(current, incoming)`** เป็น engine-level
  simplification ของกฎ RAW ที่จริงเป็นตัวเลือกผู้เล่น (engine ไม่รู้จัก concept ผู้ใช้)

บทเรียนจาก code review Phase 2 (พบ 2 รอบ ตอนรอบแรก approve แต่มีข้อควรแก้ 3 ข้อ แก้แล้วผ่านรอบ 2):
damage รวมต้อง clamp ไม่ให้ติดลบ **ก่อน** เข้า resistance math เสมอ (ไม่ใช่ clamp ทีหลัง) และ resolver
ที่หา "เต๋าตัวเดียวที่ควรมี" (เช่น d20 ของ attack roll) ต้องเช็คทั้งกรณี "ไม่มีเลย" และ "มีเกิน 1 ตัว"
ไม่ใช่แค่กรณีแรก — สองแพทเทิร์นนี้เจอซ้ำได้ใน Phase 3 เช่นกัน (death save ก็เป็น d20 เดี่ยว)

Guard rail ของ parser ที่ล็อกไว้ (เทสต์อ้างอิงค่าพวกนี้ ห้ามแก้เงียบ ๆ):
`MaxDiceCountPerGroup=100` · `MaxGroupsPerExpression=20` · `MaxSides=1000` · `MinSides=2` · `MaxExpressionLength=200`

บทเรียนจาก security review Phase 1: **.NET regex `\d` เป็น Unicode-aware** จับเลขไทย/อารบิก-อินดิก/fullwidth ด้วย
แต่ `long.TryParse` ไม่รับ → input พวกนี้เคยหลุดผ่าน regex ไปตายที่ชั้นลึกกว่าด้วยข้อความผิดความจริง
(ยัง fail closed ไม่ใช่ช่องโหว่ แต่ทำให้ debug ยาก) — **ใน parser ที่รับ input ภายนอก ให้ใช้ `[0-9]` เจาะจงเสมอ**

**⚠️ ยังไม่ได้แก้ (ยกไปชั้น API/frontend)** — ข้อความ `DiceExpressionException` สะท้อน input ดิบกลับมาด้วย
(เช่น `Expression syntax ไม่ถูกต้อง: '<script>...'`) ตัว engine ไม่ผิดเพราะเป็น library แต่**ชั้นที่เอาไปแสดงผล
ต้อง escape ก่อนเสมอ** ไม่งั้นกลายเป็น reflected XSS

โครงสร้างที่เพิ่มจาก Phase 3 (ต่อยอด `AttackResolver`/`HitPointResolver` เดิม ไม่คำนวณ massive damage ซ้ำ):
- `src/DnDEz.Rules/DeathSaveResolver.cs` — death save state machine (SRD 5.2) ทอย `"1d20"` **ไม่มี
  modifier** ผ่าน `DiceEngine.RollSystem` เดิม (รองรับ advantage/disadvantage แต่ adv/disadv มีผลแค่
  เลือกลูกที่เก็บ ไม่ใช่กติกาพิเศษของ death save) · **nat20 = ฟื้นทันที** (จบกระบวนการทั้งหมดกลับสู่
  `NotDying` พร้อม flag `RevivedWithOneHp` ให้ caller ไปเรียก `HitPointResolver.ApplyHealing` เอง — ไม่ใช่
  แค่ +1 success) · **nat1 = นับ 2 failures** · ดิบ 10-19 = success, ดิบ 2-9 = failure ·
  Successes/Failures clamp ที่ 3 เสมอ · สะสม success ครบ 3 → `Stable` และหยุดทอย, ครบ 3 failure → `Dead`
  · `ApplyAttackedAtZeroHp` แยกต่างหากสำหรับ "โดนโจมตีตอน 0 HP" (1 failure อัตโนมัติ, 2 ถ้า crit — ไม่ผ่าน
  การทอยลูกเต๋า) · ใช้แพทเทิร์นหา "เลขดิบบน d20 ที่ถูกเก็บจริง" เดียวกับ `AttackResolver` ทุกประการ
  (throw ทั้งกรณีไม่มี d20 ที่ไม่ถูก drop เลยและกรณีมีเกิน 1 ตัว) · `DeathSaveStatus` ประกาศเอง 4 ค่า
  (`NotDying/Rolling/Stable/Dead`) ต่างจาก `DnDEz.Domain.Enums.DeathSaveStatus` (3 ค่า) โดยตั้งใจ เพราะ
  ต้องมีสถานะ "ไม่ได้กำลัง 0 HP" แยกจาก "กำลังทอยอยู่" — แปลง 4→3 ค่าเป็นหน้าที่ชั้น application
  · **massive damage instant death (Phase 2's `HitPointResolver.ApplyDamageResult.IsInstantDeath`) เป็น
  คนละกลไกกับ death save โดยสิ้นเชิง — resolver นี้ไม่มี logic คำนวณซ้ำ** ถ้า `IsInstantDeath=true`
  caller เซ็ต `Dead` ตรง ๆ โดยไม่ผ่าน death save เลย
- `src/DnDEz.Rules/ActionEconomyResolver.cs` — resource tracking ล้วน ไม่มี RNG/timer เกี่ยวข้อง
  (`StartTurn`/`SpendAction`/`SpendBonusAction`/`SpendReaction`/`SpendMovement`) — action/bonus action
  ใช้ได้ 1 ครั้ง/เทิร์น, **reaction reset เฉพาะตอน `StartTurn` เท่านั้น** (ไม่รีเซ็ตตอนคนอื่นขึ้นเทิร์น),
  movement หักทีละส่วนได้ไม่ใช่ all-or-nothing · double-spend/เกินคงเหลือ = throw (caller bug ตาม
  convention เดียวกับ resolver อื่น) · **reaction timeout เป็นหน้าที่ชั้น application/Hangfire ไม่ใช่
  engine** — ในนี้ไม่มี timer/async ใด ๆ ทั้งสิ้น (ข้อบังคับสถาปัตยกรรมจาก §3 "Reaction timeout เป็น
  timer ฝั่ง server" ยังต้องทำที่ชั้นบน ไม่ใช่ในไฟล์นี้)

**🎉 rules engine ครบ 3 phase แล้ว (2026-08-22)** — `src/DnDEz.Rules` + `tests/DnDEz.Rules.Tests`
221 เทสต์ผ่านจริง ครอบ dice notation, advantage/disadvantage, seeded replay, check/save, attack/AC,
crit, damage+resistance, HP/temp HP, death save, action economy — **ยังไม่มี**: spellcasting เต็มรูป,
fog of war (รอ grid/geometry layer), condition effects (poisoned/prone ฯลฯ ที่กระทบ check/attack)

### 🔜 ขั้นถัดไป — Dev plan แบบ sprint (เสนอ 2026-08-22 · รอยืนยัน โดยเฉพาะกลไก auth)

ตอนนี้มี DB+RLS กับ rules engine เสร็จแล้วแต่**ยังไม่มีอะไรเรียกใช้จริง** ลำดับที่เสนอเน้นทำให้ครบ
loop เล็ก ๆ end-to-end ก่อน (ทดสอบ/เดโมได้จริงทุก sprint) แทนที่จะสร้างทุกชั้นค้างไว้พร้อมกันแล้วมาต่อ
ตอนท้ายซึ่งเสี่ยงเจอปัญหา integration สะสม

**Sprint A — API foundation** (บล็อกทุกอย่างที่เหลือ ต้องทำก่อน) — **แบ่ง 3 part** (2026-08-22)
เพื่อไม่ต้องรอการตัดสินใจ auth mechanism ที่ยังไม่ยืนยันก่อนเริ่มลงมือ (auth ใช้จริงถึง Part 2)

- **Part 1** ✅ **เสร็จ 2026-08-22** — scaffold `DnDEz.Api` + ต่อ EF Core (ของเดิม ไม่สร้างใหม่) ด้วย
  role `dndez_app` (ไม่ใช่ owner) + endpoint พิสูจน์ fail-closed RLS ผ่าน HTTP จริง — ผ่าน code review +
  security review ทั้งคู่ — 3 เทสต์ integration ใหม่ผ่านจริงกับ Postgres จริง (รวม 224 เทสต์ทั้ง repo)
  **ไม่มี auth ใน part นี้ตามแผน**
  - 🔴 **พบช่องโหว่จริงระหว่างทำ** (ไม่ใช่ของ Part 1 สร้างขึ้น มีมาตั้งแต่ schema เดิม) — ตาราง
    `campaigns` เองไม่เคยมี RLS มาก่อนเลย เพราะ policy เดิมสร้างจาก loop ที่กวาดหาแต่ตารางที่มีคอลัมน์
    `campaign_id` แต่ `campaigns` ใช้ `id` เป็น PK จึงหลุด loop ไป → role `dndez_app` อ่าน/เขียนทุก
    แคมเปญของทุกคนได้หมดแบบ fail-open **แก้แล้ว** ด้วย migration ใหม่ `AddCampaignsRowLevelSecurity`
    ยืนยัน apply บน Postgres จริงแล้ว — รายละเอียดเต็มดู `db-schema.md` "บทเรียนที่ 2"
  - ✅ **ช่องโหว่ต่อเนื่องแก้แล้ว (2026-08-25)** — เดิมสร้างแคมเปญใหม่ผ่าน `dndez_app` ไม่ได้เลย
    (circular bootstrap: insert แถวแรกใน `campaign_members`/`campaigns` ต้องมี `app_is_member()`
    เป็นจริงก่อน ทั้งที่กำลังสร้างแคมเปญนั้นอยู่พอดี) — แก้ด้วย migration
    `AddCampaignCreationBootstrapFunction` (Postgres function `app_create_campaign()` แบบ
    `SECURITY DEFINER`, insert `campaigns`+`campaign_members` atomic, `owner_user_id`/DM user_id
    มาจาก `app_current_user_id()` เท่านั้นห้ามรับจากภายนอก กัน backdoor) ทดสอบจริงครบ 4 เคสผ่าน
    role `dndez_app` (`tests/DnDEz.Api.Tests/CampaignBootstrapTests.cs`) รวม negative case ว่า
    INSERT ตรง ๆ ไม่ผ่าน function ยังถูก RLS บล็อกเหมือนเดิม — รายละเอียดเต็มดู `db-schema.md`
    (**หมายเหตุ: งานนี้ทำนอก `<tier>` — ไม่ผ่าน code review/security review แบบ Sprint A Part 1-2**
    ตรวจเองด้วยการทดสอบจริงกับ Postgres จริงครบทั้ง happy path และ negative case แทน)
- **Part 2** ✅ **เสร็จ 2026-08-25** — ⚠️ **auth mechanism ปรับจากข้อเสนอเดิม** (พบตอนเริ่ม Part 2):
  ตาราง `users` มี `PasswordHash` column อยู่แล้วแบบ minimal (`Id/Email/DisplayName/PasswordHash/
  CreatedAt`) ไม่ใช่ schema ที่ออกแบบรับ **ASP.NET Core Identity เต็มรูป** ซึ่งต้องการตารางคู่ขนาน
  (`AspNetUsers`/`AspNetRoles`/`AspNetUserClaims` ฯลฯ) ทับ schema เดิม → **เปลี่ยนเป็น lightweight
  custom auth**: ใช้ `PasswordHasher<T>` (utility hashing algorithm จาก Identity package แต่ไม่ดึง
  framework เต็ม ไม่มี `AddIdentity<>()`) เก็บ hash ใน `users.password_hash` เดิม + ออก **JWT bearer
  token** ตอน login (ไม่ใช่ cookie — เข้ากับสถาปัตยกรรม API-first/PWA frontend มากกว่า) ·
  quick-play ยังใช้กลไก **claim token ที่มีอยู่ใน schema แล้ว** (`Character.ClaimToken`/
  `ClaimExpiresAt`) เหมือนเดิม ไม่ต้องสร้างอะไรใหม่ แค่ไม่ผูก `OwnerUserId`
  · middleware `SET LOCAL app.user_id/campaign_id` ต่อ request ภายใน transaction เดียวกันเป๊ะ
  (จุดเสี่ยงที่เตือนไว้หลายรอบเรื่อง connection pooling + RLS)
  · **ทดสอบ RLS ข้ามแคมเปญผ่าน HTTP จริง** (บทเรียนเดียวกับตอนทดสอบ RLS รอบแรกที่เจอช่องโหว่จากการ
  ทดสอบจริง ไม่ใช่อ่านโค้ด) — seed แคมเปญ/สมาชิกผ่าน owner role โดยตรง (ข้าม bootstrap gap ของ
  Part 3 ที่ยังไม่แก้ เพราะ Part 2 ไม่ต้องสร้างแคมเปญจริงผ่าน API)

  ผ่าน code review 2 รอบ (รอบแรกเจอบั๊กจริง — `catch (DbUpdateException)` ใน `/auth/register`
  กว้างเกินไป ยืนยันด้วยการรันจริงว่า `DisplayName` เกิน `varchar(100)` ถูกจัดผิดเป็น
  `email_already_registered` แก้ด้วยแยก `PostgresException.SqlState == "23505"` แล้ว `throw;`
  รักษา stack trace สำหรับ error อื่น รอบ 2 approve ไม่มี blocker) + security review pass ไม่มี
  critical (escalate ได้แต่ reviewer ประเมินว่าไม่จำเป็นเพราะ verify ได้หมดด้วยหลักฐานโค้ดจริง —
  algorithm confusion ปิดถูก, timing-safe login ยืนยันเชิงประจักษ์, SET LOCAL parameterized ไม่มี
  injection surface) — มี backlog สำหรับก่อน production จริง (ไม่บล็อก Part 2): rate limiting บน
  `/auth/login`/`/auth/register`, password max-length cap (กัน CPU-exhaustion DoS ผ่าน PBKDF2),
  log เตือนตอนใช้ dev JWT fallback key, `UseHttpsRedirection()`, JWT revocation/refresh-token

  ⚠️ **บทเรียนกระบวนการ**: dispatch งาน green ไปยัง `sonnet-worker` 3 รอบติดกันไม่คืบหน้าเลย
  (มี tool call จริงแต่ไม่เคยแก้ไฟล์ที่ต้องสร้างใหม่) — วินิจฉัยว่า **`sonnet-worker` ไม่มี `Write`
  tool** (มีแค่ Read/Grep/Glob/Bash/Edit/Agent ตาม `CLAUDE.md`) จึงสร้างไฟล์ใหม่ไม่ได้ escalate ไป
  `opus-worker` (มี `Write`) จึงสำเร็จ — **งานที่ต้องสร้างไฟล์ใหม่ ควร dispatch ไป opus-worker ตรง ๆ
  ไม่ต้องเสีย 3 รอบกับ sonnet-worker ก่อน** (งานที่แก้ไฟล์เดิมอย่างเดียวยังส่ง sonnet-worker ได้ปกติ
  ยืนยันแล้วตอนแก้ finding รอบ 2 ที่ sonnet-worker ทำสำเร็จรอบเดียวเพราะเป็นแค่ Edit)
- **Part 3** ✅ **เสร็จ 2026-08-26** — thin CRUD (create+list+get เท่านั้น ไม่มี update/delete) ของ
  campaign/session/character — **🎉 Sprint A ครบ 3 part แล้ว**
  - `POST/GET /campaigns`, `GET /campaigns/{id}` — สร้างผ่าน `app_create_campaign()` เท่านั้น
    (ห้าม `db.Campaigns.Add()` ตรง ๆ เด็ดขาด — ไม่มี INSERT policy บนตารางนี้แล้ว)
  - `POST/GET /campaigns/{id}/sessions`, `GET .../sessions/{sessionId}` — ใช้ EF Core ปกติ
    (policy เดิมของ sessions ไม่มีปัญหา circular bootstrap)
  - `POST/GET /campaigns/{id}/characters`, `GET .../characters/{characterId}` — `OwnerUserId` มาจาก
    JWT claim เท่านั้น (`principal.GetUserId()`) ห้ามรับจาก request body — list ไม่ส่ง `Sheet` (jsonb เต็ม)
  - **แพทเทิร์นที่ต้องรักษาไว้ในทุก endpoint ลูก**: validate input ก่อนแตะ DB เสมอ + เช็ค
    `CampaignVisibility.IsVisibleAsync` (มองเห็นแคมเปญไหม จาก RLS) **ก่อน** insert เสมอ — เพราะ
    request ทั้งหมดอยู่ใน transaction เดียวที่ middleware เปิดไว้ ถ้าปล่อยให้ RLS `WITH CHECK` เป็น
    คนดักตอน insert transaction จะ abort กลายเป็น 500 แทนที่จะเป็น 400/404 ที่ควรเป็น
  - ไม่ใช่สมาชิกแคมเปญ = **404 เสมอ** (ไม่ใช่ 403, ไม่ใช่ array ว่าง) — เพราะ RLS ทำให้แถวหายเหมือนกัน
    ทั้ง "ไม่มีอยู่จริง" และ "มีอยู่แต่ไม่ใช่สมาชิก" ป้องกัน enumeration ผ่านความต่างของ error code

  ผ่าน code review (approve, 3 ข้อเสนอแนะเล็กน้อยไม่บล็อก: enum parser ยอมรับ string ตัวเลขดิบ,
  `CharacterEndpoints` ไม่ trim ชื่อ [ต่างจาก `CampaignEndpoints` ที่ trim], ไม่มีเทสต์ผสม
  validation-fail + non-member พร้อมกัน) + security review (approve, 2 informational ไม่บล็อก:
  `Sheet` เห็นได้ทุกสมาชิกในแคมเปญไม่จำกัดแค่ owner/DM — เป็น design ที่ตั้งใจ ไม่ใช่บั๊ก, pattern
  authorization ไม่ consistent ระหว่างไฟล์ — `campaigns` พึ่ง RLS อย่างเดียวไม่มี C# check ซ้ำ
  ต่างจาก session/character ที่เช็คสองชั้น ทั้งคู่ fail-closed ได้จริงเหมือนกัน แค่ style ต่างกัน)

  ทดสอบจริง 295/295 ผ่าน (221 Rules + 74 API)

  **สิ่งที่ยังไม่ทำ (ตั้งใจ ไม่ใช่ scope creep)**: update/delete endpoint, role check DM-only
  (ทุกสมาชิกสร้าง session/character ได้เท่ากันตอนนี้), join-campaign API (เทสต์ seed
  membership ผ่าน owner role ตรง ๆ), quick-play/companion mode flow

  **RLS foundation ที่ต้องมีก่อน Part 3 นี้ทำงานได้** (ทำนอก tier dispatch โดย main โดยตรง เพราะเป็น
  งานตัดสินใจต่อเนื่องที่ต้องเห็นภาพรวมพร้อมกัน — ทั้งคู่ผ่าน security review แยกต่างหากแล้ว):
  - `app_create_campaign()` (bootstrap gap fix, ดู db-schema.md)
  - `app_is_member_of(uuid)` + policy `campaign_member_read` — พบระหว่างออกแบบ Part 3 ว่า policy เดิม
    ของ `campaigns` ผูกกับ `app.campaign_id` ตัวเดียวเสมอ ทำให้ **"list แคมเปญของตัวเอง" เป็นไปไม่ได้
    เลยในทางโครงสร้าง** (ไม่ใช่แค่ `campaigns` — `campaign_members` เดิมก็ติดปัญหาเดียวกัน) แก้ด้วย
    ฟังก์ชันใหม่ที่เช็คสมาชิกภาพทีละแถวแทนการผูกกับ session variable ตัวเดียว รายละเอียดเต็มใน
    `db-schema.md`

**Sprint B — ต่อ rules engine เข้า API จริง** (proof แรกว่าสถาปัตยกรรมทำงาน) — **แบ่ง 3 part**
(2026-08-26) ตามบทเรียนจาก Sprint A: dispatch ที่ scope กว้างเกินไปทำให้ worker เสียเวลา/token
ไปกับการสำรวจเอง (เจอกับ opus-worker ที่อ่าน context ~72 นาทีก่อนเริ่มเขียนโค้ดเลยด้วยซ้ำตอน
Sprint A Part 3) — แบ่งให้แต่ละ part มีไฟล์ที่ต้องอ่านเป็นรายการปิด ไม่ใช่ "ไปสำรวจเอง"

- **Part 1** ✅ **เสร็จ 2026-08-26** — Dice roll endpoint
  `POST /campaigns/{campaignId}/sessions/{sessionId}/dice-rolls` เรียก `DiceEngine` ที่มีอยู่แล้ว
  (221 เทสต์ผ่านจาก Phase 1) persist ลง `dice_rolls` ผ่าน EF Core ปกติ (ไม่มีปัญหา circular RLS
  แบบ `campaigns` — reuse pattern เดียวกับ sessions/characters ได้ตรง ๆ) เป็น**จุดแรกที่พิสูจน์ว่า
  "AI ตัดสินใจเรียก tool, rules engine คำนวณ, DB persist" ทำงานได้จริงในโค้ด**
  - รองรับแค่ `DiceMode.SystemRoll` — แคมเปญที่ตั้ง `DmEntered` ถูกปฏิเสธด้วย 400 ชัดเจน (ไม่ silent
    fallback ซึ่งจะขัดกับ anti-cheat ที่ล็อกไว้ใน player-side-design §5) — DmEntered ค้างไว้ทำทีหลัง
  - seed generate ใหม่จาก CSPRNG ทุกครั้ง (`SeededDiceRoller.CreateWithSecureRandomSeed()`) เก็บ
    ลง DB จริงแต่**ไม่มีฟิลด์ Seed ใน response เลย** (DTO ไม่มี property นี้ตั้งแต่ต้น — การันตีด้วย
    type system ไม่ใช่แค่ runtime filter) มีเทสต์ยืนยันตรง ๆ
  - `character_id` เป็น `null` เสมอในรอบนี้ (ตั้งใจ ไม่ผูก character จนกว่าจะถึง Part 3)
  - ทดสอบจริง 85/85 ผ่าน (74 Part 3 เดิม + 11 ใหม่) รวม 306 เทสต์ทั้ง repo
  - ⚠️ **งานนี้ทำโดยไม่มี `<tier>` นำหน้า** (ผู้ใช้สั่งตรง ไม่ผ่าน tier routing) — **ไม่ผ่าน
    code review/security review แบบ Sprint A** ตรวจเองด้วยการอ่านโค้ดซ้ำละเอียด + ทดสอบจริงกับ
    Postgres จริงครบทุก edge case (validation order, membership check, DmEntered rejection,
    seed ไม่รั่ว, cross-campaign session isolation)
- **Part 2** ✅ **เสร็จ 2026-08-26** — ตัดสินใจ character sheet ↔ rules engine mapping: **เลือก
  ตัวเลือก (ก) promote เฉพาะ HP/AC/resistance เป็น typed column** (migration
  `AddCharacterCombatStats`) เหตุผล: "ทุกตัวละครมีแน่นอน ไม่ผันแปรตามคลาส" ต่างจากของที่เหลือใน
  `Sheet` (spell list, inventory, feature text) ที่ผันแปรจริง — ของสองแบบอยู่ร่วมกันได้ ไม่ได้
  ยกเลิก jsonb sheet ทำให้ DB บังคับ invariant ได้ตรง ๆ (`ck_characters_current_hp` ฯลฯ เหมือน
  `ck_character_resources_range` ที่มีอยู่แล้ว) และ Sprint B Part 3 อ่าน HP/AC ตรงเป็น int ได้เลย
  ไม่ต้อง parse jsonb ทุกครั้ง
  - `characters` เพิ่ม 7 column: `current_hp`, `max_hp`, `current_temp_hp`, `armor_class` (int) +
    `damage_resistances`/`damage_vulnerabilities`/`damage_immunities` (`text[]`, validate ด้วย
    `Enum.TryParse<DnDEz.Rules.DamageType>` ไม่ hardcode รายชื่อซ้ำ — CHECK constraint ที่ DB ก็อิง
    13 ชนิดเดียวกัน)
  - `POST /campaigns/{id}/characters` (Sprint A Part 3) ขยายรับ field ใหม่ทั้งหมดแบบ **optional
    ทั้งหมด ไม่ breaking** — ไม่ส่งมา = fallback เป็น safe placeholder (MaxHp=10, AC=10, resistance
    sets ว่าง) ไม่ส่ง CurrentHp = เต็มเลือดตาม MaxHp ที่ resolve แล้วเสมอ (ตัวละครใหม่เริ่มเต็มเลือด)
    `GET .../characters/{id}` คืนค่าทั้งหมดกลับมาด้วย
  - ทดสอบจริง 94/94 ผ่าน (85 เดิม + 9 ใหม่) รวม CHECK constraint ที่ยืนยันด้วย raw SQL ตรง ๆ ก่อน
    เขียนโค้ด C# (current_hp>max_hp, max_hp=0, damage type ปลอม — ทุกเคส fail ตามที่ตั้งใจ)
  - ⚠️ **หมายเหตุ backward-compat**: เทสต์เดิมบางตัวใน `CharacterCrudTests.cs` (ก่อน Part 2) seed
    `Sheet` jsonb ด้วย `{"hp": 24}` ตามสมมติฐานเดิมที่ HP อยู่ใน sheet — ค่านั้น**ไม่เกี่ยวกับ**
    `CurrentHp`/`MaxHp` column ใหม่เลย (คนละที่เก็บกัน) ยังผ่านเทสต์ปกติเพราะเป็นแค่การอ่าน jsonb
    ดิบกลับมา ไม่ใช่ที่มาของค่า HP จริงที่ rules engine จะใช้ต่อจากนี้
  - ⚠️ **งานนี้ทำโดยไม่มี `<tier>` นำหน้า** เหมือน Part 1 — ไม่ผ่าน code/security review แบบ
    Sprint A ตรวจเองด้วยการทดสอบจริงกับ Postgres จริงครบทุกจุด (CHECK constraint, backward
    compatibility, validation order)
- **Part 3** ✅ **เสร็จ 2026-08-26** — Check/attack/damage resolution: 2 endpoint ใหม่ต่อ
  `CheckResolver`/`AttackResolver`/`DamageResolver`/`HitPointResolver` เป็น flow เดียวกันครั้งแรก
  (Part 1 มีแค่ `DiceEngine` เดี่ยว ๆ)
  - `POST /campaigns/{id}/sessions/{id}/characters/{characterId}/checks` — body
    `{ Expression, Advantage?, Dc (1..40, required) }` → ทอย 1d20+mod เทียบ `Dc` ผ่าน
    `CheckResolver` เก็บลง `dice_rolls` โดยผูก `character_id` จริงเป็นครั้งแรก (Part 1 ตั้งใจปล่อย
    `null` ไว้ก่อน)
  - `POST /campaigns/{id}/sessions/{id}/characters/{characterId}/attacks` — body
    `{ Expression, Advantage?, TargetCharacterId, DamageComponents: [{Expression, Type}] (1..20) }`
    → ทอย attack roll ผ่าน `AttackResolver` เทียบ AC ของเป้าหมาย **ทอย damage เฉพาะตอน
    hit/critical เท่านั้น** (miss/automatic-miss ไม่แตะ `DamageResolver`/`HitPointResolver` เลย
    ไม่ใช่แค่ = 0) หัก resistance/vulnerability/immunity ของเป้าหมายผ่าน `DamageTypeResolver` แล้ว
    เอาผลไป `HitPointResolver.ApplyDamage` เขียน `current_hp`/`current_temp_hp` ของเป้าหมายจริง —
    persist ทั้ง attack roll และทุก damage component เป็นแถว `dice_rolls` แยกกัน (คนละ
    seed/roller ต่อ "หนึ่งเหตุการณ์ทอย" ตามกฎเดิม) ผูก `character_id` เป็นผู้โจมตีเสมอ
  - รองรับแค่ `DiceMode.SystemRoll` เหมือน Part 1 (DmEntered → 400) · seed ไม่โผล่ใน response
    เด็ดขาดทั้งสอง endpoint (เหมือน Part 1) · validate expression/damage-type ให้ผ่านทุกจุดแบบ
    pure (parse อย่างเดียว ไม่ทอย ไม่แตะ DB) ก่อนแตะ DB หรือ CSPRNG เสมอ กัน "ทอย attack ไปแล้ว
    แต่ damage component ผิด format" ต้อง rollback ทั้ง transaction โดยไม่จำเป็น
  - ทดสอบจริง 118/118 ผ่าน (94 เดิม + 24 ใหม่ ใน `CombatResolutionTests.cs`) รวม 339 เทสต์ทั้ง repo
    — checks endpoint ทดสอบแบบ deterministic เต็มรูป (Dc=1 การันตี success, Dc=21 การันตี fail
    จากช่วงของ 1d20) ส่วน attacks ทดสอบด้วย AC สุดขั้ว (1/50) + retry loop ที่ bound รอบไว้ชัดเจน
    เพื่อข้าม 5% ของ AutomaticMiss/CriticalHit ที่ override AC เสมอ — **เจอบั๊กจริงระหว่างเขียนเทสต์
    เอง**: retry loop ของเทสต์ miss-path รอบแรกใช้ target ตัวเดียวกันซ้ำทุกรอบ ถ้ารอบต้น ๆ ดัน
    critical hit (5%) ดาเมจจะ apply ลง DB จริงถาวรก่อนจะเจอรอบที่ miss ทำให้ HP ก่อน/หลังไม่ตรงกับ
    ที่จับไว้ตอนต้น — แก้โดยสร้าง target ใหม่ทุกรอบ retry (throwaway target ต่อ attempt)
  - ⚠️ **งานนี้ทำโดยไม่มี `<tier>` นำหน้า** เหมือน Part 1/2 — ไม่ผ่าน code/security review แบบ
    Sprint A ตรวจเองด้วยการอ่านโค้ดซ้ำละเอียด + ทดสอบจริงกับ Postgres จริง (รวมถึงเจอ/แก้บั๊ก
    test isolation ข้างต้นด้วยตัวเอง)
  - **เก็บไว้นอกขอบเขต Part นี้โดยตั้งใจ**: ไม่ผูกกับ `DeathSaveState`/`ActionEconomyResolver` แม้
    `ApplyDamageResult` จะมี `DroppedToZero`/`IsInstantDeath` ให้พร้อมใช้แล้วก็ตาม (response คืนค่า
    สองฟิลด์นี้ไว้เป็นข้อมูลอย่างเดียว) — การตัดสินใจว่า "HP ถึง 0 ต้องเข้า dying state อัตโนมัติ
    หรือ DM ต้องยืนยันก่อน" เป็นการตัดสินใจ UX/gameplay ที่ต้องออกแบบแยก ไม่ใช่แค่ wiring โค้ด

ทุก part มี integration test จริงกับ Postgres จริงในตัว (ไม่ใช่ mock) ตาม TDD flow ปกติของ
project — Part 1 คือจุดแรกที่พิสูจน์หลักการ "AI ตัดสินใจเรียก tool, rules engine คำนวณ, DB
persist" ที่ล็อกไว้ตั้งแต่ต้นทำงานได้จริงในโค้ด ไม่ใช่แค่เอกสาร

**Sprint C — Realtime ขั้นต่ำสุด** (2026-08-26) ผ่าน `<tier>` ตามหลัก decision framework: แตกเป็น
3 part ได้จริง (infra+auth → wire dice-roll → wire HP-update)

- **Part 1** ✅ **เสร็จ 2026-08-26** — SignalR hub เดียว (`CampaignHub`) + Redis backplane
  (`docker-compose.yml` มี service `redis` อยู่แล้วตั้งแต่ต้น แค่ยังไม่เคย `up -d`) **ยังไม่
  broadcast อะไรจริง** (แค่โครง connection/group/authorization — ของจริงเป็น Part 2/3)
  - **ทำเองตรง ๆ ไม่ dispatch** แม้มี `<tier>` — เพราะเจอการตัดสินใจสถาปัตยกรรมที่ต้องเห็นภาพรวม
    พร้อมกัน (ตาม decision framework exception): middleware ที่ตั้ง `SET LOCAL app.user_id` ให้
    RLS ผูกกับ**หนึ่ง HTTP request**เท่านั้น (`app.Use(...)` วิ่งตอน handshake ครั้งเดียว) —
    หลัง WebSocket upgrade แล้ว Hub method invocation ถัดไปทุกครั้ง**ไม่ผ่าน middleware pipeline
    นั้นซ้ำอีกเลย** (SignalR dispatch ตรงไป HubDispatcher) ถ้าใช้ DbContext ที่ inject ผ่าน
    constructor ปกติ (scoped ผูกกับ connection ทั้งชีวิต) จะเจอบั๊กคลาสเดียวกับที่เคยเจอมาแล้วจริง
    ในโปรเจกต์นี้ (connection pooling + RLS) — แก้ด้วย `CampaignHub.IsMemberAsync` เปิด
    `IServiceScopeFactory.CreateScope()` + transaction สั้น ๆ ของตัวเองต่อการเช็คสิทธิ์หนึ่งครั้ง
    (เหมือนที่ middleware ทำต่อ HTTP request แต่ทำเองต่อ Hub invocation แทน)
  - `JoinCampaign`/`LeaveCampaign` join ได้เฉพาะสมาชิกแคมเปญจริง — ไม่ใช่สมาชิก/แคมเปญไม่มีจริง
    ได้ `HubException("campaign_not_found")` ข้อความเดียวกันทั้งคู่ (กัน enumeration เหมือน REST 404)
  - JWT ผ่าน query string (`?access_token=`) รับเฉพาะ path `/hubs/campaign` เท่านั้น (browser
    WebSocket ตั้ง custom header ตอน handshake เองไม่ได้) REST endpoint อื่นทุกตัวยังคงต้องใช้
    `Authorization` header เท่านั้น — มีเทสต์ยืนยันตรง ๆ ว่า REST endpoint reject token จาก query
    string แม้ token จะ valid จริงก็ตาม (เพิ่มหลัง code review แนะนำ)
  - ทดสอบจริง 123/123 ผ่าน (117 เดิม + 6 ใหม่ ใน `CampaignHubTests.cs`, ใช้
    `Microsoft.AspNetCore.SignalR.Client` ต่อ `WebApplicationFactory` จริง + Redis จริง ไม่ mock)
    รวม 344 เทสต์ทั้ง repo — **ผ่านตั้งแต่รอบแรกที่รัน** (ยืนยันว่า design การแก้ RLS-per-invocation
    ถูกต้องจริง ไม่ใช่แค่ทฤษฎี)
  - ผ่าน **Code Review** (approve ไม่มี blocker เสนอเพิ่มเทสต์ query-string-token-ต้อง-reject-ที่
    REST — เพิ่มแล้ว) และ **Security Review** (ไม่มี Critical escalate 2 ข้อขึ้น opus-worker ตาม
    trigger auth/session-management ซับซ้อน — main ตัดสินใจเองไม่ spawn opus-worker เพิ่มเพราะมี
    ข้อมูลพอ ดูรายละเอียดเต็มใน `.claude/logs/routing.md`):
    1. **แก้แล้ว**: `dotnet list package --vulnerable` (security-reviewer รันไม่ได้เพราะ sandbox
       ไม่มี SDK 8 — main รันเองได้จริงเพราะมี SDK 8.0.423) เจอ `MessagePack 2.5.108` (transitive
       จาก `Microsoft.AspNetCore.SignalR.StackExchangeRedis`) มีช่องโหว่ High/Moderate จริง 12
       รายการ — pin `PackageReference` MessagePack เป็น `3.1.8` ตรง ๆ ทั้ง `DnDEz.Api.csproj`
       และ `DnDEz.Api.Tests.csproj` (override ไม่ไหลข้าม `ProjectReference` ต้องประกาศซ้ำสองที่)
       ยืนยันสะอาดทั้งคู่ + 344/344 เทสต์ยังผ่านหลัง bump
    2. **backlog ไม่บล็อก** — JWT ผ่าน query string เป็น bearer token เต็ม (ไม่ใช่ one-time
       ticket อายุสั้นแยก audience) reviewer เสนอว่า ticket-exchange ปลอดภัยกว่าจริง แต่ไม่ได้เปิด
       ช่องโหว่ใหม่เกินกว่าที่ทั้งระบบยอมรับอยู่แล้ว (token อายุสั้น 1 ชม. ไม่มี revocation ทั่วทั้ง
       API) — เก็บไว้ก่อน production เหมือน rate limiting/password-max-length ที่ Sprint A Part 2
       บันทึกไว้แล้ว
    3. **ต้องแก้ก่อนมี feature ถอนสมาชิก (ยังไม่ใช่วันนี้)** — group membership ไม่ re-verify หลัง
       join สำเร็จ (ถูกถอนสิทธิ์กลางเซสชันจะยังอยู่ใน group ต่อจนกว่า socket จะหลุด) **ยืนยันแล้วว่า
       ยัง exploit ไม่ได้จริงตอนนี้** เพราะทั้งระบบยังไม่มี endpoint ลบ/ถอนสมาชิกแคมเปญเลยสักตัว
       (grep ทั้ง repo ไม่เจอ ตรงกับที่ Sprint A Part 3 บันทึกไว้แล้วว่า "ยังไม่ทำ") — ต้องกลับมาแก้
       (เช่น re-check membership ก่อน broadcast ทุกครั้ง หรือ evict group ตอน membership เปลี่ยน)
       ทันทีที่มี feature ถอน/ลบสมาชิกเกิดขึ้นจริง ไม่ใช่ปล่อยผ่านถาวร
    4. **backlog ที่ต้องจับตาตอน Part 2/3** — `JoinCampaign` ไม่มี rate limit ต่อ connection เลย
       แต่ต่างจาก "ไม่มี rate limit ทั่วทั้งระบบ" (backlog เดิมของ `/auth/login`) ตรงที่ WebSocket
       เดียวเรียก Hub method รัวได้โดยไม่มี HTTP round-trip คั่นเหมือน REST — client ตัวเดียว spam
       `JoinCampaign` ด้วย GUID สุ่มรัว ๆ อาจดูด connection จาก Npgsql pool ที่ใช้ร่วมกับ REST
       endpoint ทั้งหมดจนหมดเร็วกว่า — ยังไม่บล็อก Part 1 (ยังไม่มี broadcast ของจริงให้กังวลเรื่อง
       cost) แต่ควรใส่ throttle ก่อน/พร้อม Part 2/3 ที่เริ่มมี broadcast จริงที่มีต้นทุนต่อครั้งแล้ว
- **Part 2** ✅ **เสร็จ 2026-08-27** — wire `DiceRolled` event เข้า `DiceRollEndpoints.cs`
  (broadcast เข้า `CampaignHub.GroupName(campaignId)` ทันทีหลัง `SaveChangesAsync()` สำเร็จ)
  - **dispatch ผ่าน tier ปกติเป็นครั้งแรกของ Sprint C** (Part 1 ทำเองเพราะเป็นการตัดสินใจ
    สถาปัตยกรรม แต่ Part 2 เป็นงานเชิงกลไกล้วน ๆ ตามที่ประกาศไว้) — `sonnet-worker` รับเป็น first
    triage ตามกฎข้อ 1 ทำสำเร็จโดยไม่ต้อง escalate ขึ้น `opus-worker` เลย (แก้แค่ 2 ไฟล์ที่มีอยู่แล้ว
    ด้วย `Edit` ล้วน ไม่ชนข้อจำกัด Write tool) — ยกเว้น TDD เต็มรูปเพราะเป็นงาน passthrough/
    boilerplate ตาม CLAUDE.md ข้อ 6
  - payload ใหม่ `DiceRolledBroadcast` มีฟิลด์เหมือน `DiceRollCreatedResponse` แต่ตั้งใจไม่มี `Seed`
    เด็ดขาด (กฎเดียวกับ HTTP response — PROJECT-CONTEXT §3)
  - **ตัดสินใจไม่ dispatch security-reviewer รอบนี้** (บันทึกเหตุผลไว้ล่วงหน้าใน
    `.claude/logs/routing.md` ก่อน dispatch) เพราะไม่มี input surface ใหม่/auth decision ใหม่/
    dependency ใหม่ — code-reviewer ยืนยันตอนจบว่าไม่จำเป็นต้อง escalate ขึ้น security-reviewer จริง
  - ทดสอบจริง 127/127 ผ่าน (124 เดิม + 3 ใหม่ ใน `DiceRollCrudTests.cs` ต่อ SignalR jointly กับ
    Postgres+Redis จริง) รวม 348 เทสต์ทั้ง repo
  - ผ่าน code review — ไม่มี blocker พบ 1 design smell ที่ตั้งใจ**ไม่แก้ตอนนี้**: broadcast เกิด
    "ใน" transaction เดียวกับ request (middleware เปิดคลุมทั้ง request ใน `Program.cs`) ถ้า
    `SendAsync` throw (เช่น Redis สะดุดชั่วคราว) จะ rollback แถว `DiceRoll` ที่ persist สำเร็จไปแล้ว
    ทั้งที่ dice roll เองไม่มีปัญหาอะไรเลย — reviewer แนะนำให้ wrap try/catch แบบ log-and-swallow
    แต่เสนอให้แก้ตอน**เดียวกับ Part 3** เพราะจะมี broadcast call site ที่สองเกิดขึ้น (แก้ครั้งเดียว
    ให้ทั้งคู่สอดคล้องกันดีกว่าแพตช์ทีละจุด — ยังไม่มี pattern outbox/retry ในโปรเจกต์นี้เลยสักที่)
- **Part 3** ✅ **เสร็จ 2026-08-27 — 🎉 Sprint C ครบ 3 part แล้ว** — wire `HpUpdated` event เข้า
  `CombatEndpoints.cs` (เฉพาะ endpoint "attacks" เท่านั้น — "checks" ไม่ได้ broadcast) broadcast
  ต่อเมื่อ `targetNewHp is not null` (คือตอน hit/critical ที่มี damage จริงเท่านั้น ตรงกับ null-ness
  contract เดิมของ `AttackResolutionResponse` จาก Sprint B Part 3) + แก้ design smell ของ Part 2
  พร้อมกันตามแผน: wrap ทั้งสอง broadcast call site (`DiceRolled` ใน `DiceRollEndpoints.cs` และ
  `HpUpdated` ใหม่) ด้วย `try/catch` แบบ log-and-swallow — broadcast fail (เช่น Redis สะดุด) จะไม่
  ทำให้ transaction ทั้ง request ถูก rollback อีกต่อไป
  - **dispatch ผ่าน tier ปกติเหมือน Part 2** — `sonnet-worker` รับเป็น first triage สำเร็จโดยไม่ต้อง
    escalate ขึ้น `opus-worker` เลย (แก้ 3 ไฟล์ที่มีอยู่แล้วด้วย `Edit` ล้วน)
  - **ปัญหาทางเทคนิคที่เจอระหว่างทำ**: `ILogger<T>` ใช้กับ `CombatEndpoints`/`DiceRollEndpoints` ไม่ได้
    (`CS0718` — ห้ามใช้ static class เป็น generic type argument เพราะทั้งสองคลาสต้อง `static` สำหรับ
    extension method `this WebApplication`) แก้ด้วย `ILoggerFactory.CreateLogger(typeof(X))` แทน
    ได้ category name เดียวกับ `ILogger<X>` เป๊ะ ไม่มี DI registration เพิ่ม (`ILoggerFactory` ลงทะเบียน
    ให้อัตโนมัติจาก `WebApplication.CreateBuilder` อยู่แล้ว)
  - **เจอบั๊กจริงในเทสต์เดิมที่ไม่เกี่ยวกับงานนี้เลย** ระหว่าง sonnet-worker รัน verify:
    `Post_attacks_that_hits_rolls_damage_and_updates_target_hp` (Sprint B Part 3, ตัว main เองเขียน
    ไว้) assert `20 - body.TotalDamage == body.TargetNewHp` แบบไม่ clamp ที่ 0 — fail เป็นครั้งคราว
    เมื่อ critical hit ทอยดาเมจเกิน 20 (`HitPointResolver.ApplyDamage` clamp `NewHp` ที่ 0 จริง แต่
    เทสต์ลืม clamp ตาม) — sonnet-worker วินิจฉัยถูกต้องแล้วรายงานกลับแทนที่จะแก้เอง (นอก scope ที่สั่ง)
    main แก้เองเป็น `Math.Max(0, 20 - body.TotalDamage!.Value)` ให้ตรงกับ resolver — ยืนยันเสถียร
    3 รอบติดกันก่อน dispatch code review
  - ทดสอบจริง 129/129 ผ่าน (127 เดิม + 2 ใหม่ ใน `CombatResolutionTests.cs`) รวม **350 เทสต์ทั้ง
    repo** (221 Rules + 129 API)
  - ผ่าน code review — approve ไม่มี blocker (ยืนยันการแก้ flaky-test ตรงกับสูตร `HitPointResolver`
    จริงด้วย) มี 2 note ไม่บล็อก: (1) ไม่มีเทสต์จำลอง broadcast ล้มเหลวจริง (ไม่มี seam mock
    `IHubContext` ในสไตล์ integration test ของโปรเจกต์นี้ — เข้าใจได้ว่าทำไมไม่มี) (2) broadcast
    ยังยิงแม้ damage สุทธิ = 0 (target immune ทุก type ที่โจมตี) แต่เป็น null-ness contract เดิมจาก
    Sprint B Part 3 ไม่ใช่บั๊กใหม่จาก Part 3 นี้ — ตัดสินใจไม่ dispatch security-reviewer (เหตุผล
    เดียวกับ Part 2: ไม่มี input surface/auth/dependency ใหม่) code-reviewer ยืนยันซ้ำว่าถูกต้อง

**Sprint D — BYOK + orchestrator ขั้นต่ำ** (2026-08-27) แบ่ง 3 part: Part 1 BYOK key storage →
Part 2 tool-calling loop skeleton (ไม่มี approval gate) → Part 3 approval gate ผูก `AutonomySetting`

- **Part 1** ✅ **เสร็จ 2026-08-27** — BYOK: เก็บ/ดึง/ลบ API key ผู้ใช้แบบเข้ารหัสผ่าน ASP.NET Data
  Protection (`dm-side-design.md` §7) — `PUT/GET/DELETE /users/me/api-keys[/{provider}]`
  - **ทำเองตรง ๆ ไม่ dispatch** แม้มี `<tier>` — เจอการตัดสินใจสถาปัตยกรรมหลายจุดที่ต้องเห็นภาพรวม
    พร้อมกัน (decision framework exception):
    1. พบว่า `api_keys` table/`ApiKey` entity/`ApiKeyStatus` enum มีอยู่แล้วตั้งแต่ `InitialSchema`
       (2026-08-21) แต่ไม่เคยมี endpoint ต่อจริงมาก่อน — ต่อครั้งแรกเจอช่องโหว่จริง 2 จุด: index
       `(user_id, provider)` ไม่ unique (upsert สอง request พร้อมกันได้ 2 แถวซ้อน) และ `status`
       ไม่มี CHECK constraint (ขัดกับหลัก "enum เก็บเป็น text + มี CHECK constraint ทุกตัว" ใน
       db-schema.md) แก้ทั้งคู่ด้วย migration `AddApiKeysConstraints` ยืนยันด้วย raw SQL จริงว่า
       reject duplicate/garbage-status ตามที่ตั้งใจ + ยืนยัน RLS `own_rows_only` (ของเดิม) บล็อก
       cross-user ที่ระดับ raw SQL ตรง ๆ ด้วย (ไม่ใช่แค่ผ่าน WHERE ของ C#)
    2. Data Protection key ring persist ลง Redis เดียวกับ SignalR backplane (`ConnectionMultiplexer`
       แยกเพราะ SignalR ไม่ expose ให้ share) + **นอก Development ต้องมี certificate ป้องกัน key
       ring เอง** ไม่งั้น key-encryption-key จะไม่ถูกป้องกันเลยบน Linux —
       `DataProtectionCertificateResolver` mirror `JwtSigningKeyResolver`'s fail-fast pattern เป๊ะ
    3. เจอ+แก้ vulnerability จริงอีกจุด (นอกเหนือจาก MessagePack ของ Sprint C):
       `System.Security.Cryptography.Xml 8.0.2` (transitive จาก
       `Microsoft.AspNetCore.DataProtection.StackExchangeRedis`) มีช่องโหว่ High 8 รายการ —
       pin เป็น `10.0.11`
  - **ขอบเขตที่ตัดออกตั้งใจ**: "validate ด้วย test call ทันทีตอนกรอก" ตามที่ dm-side-design.md §7
    ระบุไว้ ยกไป Part 2 เพราะต้องมี Anthropic HTTP client อยู่แล้วตอนนั้น — key ใหม่ทุกตัวเริ่มที่
    `ApiKeyStatus.Active` แบบ optimistic, `LastValidatedAt = null` จนกว่าจะมี Part 2 · รองรับแค่
    `provider = "anthropic"` ตอนนี้
  - ผ่าน **Code Review** (round 1 ไม่บล็อกแต่ชี้ 2 จุดจริง: PUT upsert race ไม่จับ
    `DbUpdateException` เลย [catch ที่มีอยู่ผูกกับ `/auth/register` เท่านั้นไม่ใช่ global] กับเทสต์
    round-trip เดิม decrypt ผ่าน factory เดียวกับที่ encrypt ไม่ได้พิสูจน์ Redis persistence จริง —
    แก้ทั้งคู่: เพิ่ม savepoint+catch 23505→409 pattern เดียวกับ `/auth/register` เป๊ะ, เพิ่มเทสต์ใช้
    `WebApplicationFactory` ตัวที่สองแยกอิสระ decrypt ciphertext ข้าม process) และ **Security
    Review** (ไม่มี Critical ไม่ escalate opus-worker — รัน `dotnet list package --vulnerable`
    อิสระเองยืนยันซ้ำว่า pin สะอาดจริงทั้งคู่ ยืนยัน IDOR เป็นไปไม่ได้โดยโครงสร้าง [ไม่มี user-id
    param ให้ substitute เลย] และ cert protection กัน Redis-compromise-alone ได้จริง พบ 1 Warning
    จริงเพิ่ม: **ไม่มี upper bound ความยาว API key เลยทั้งชั้น API และ DB** — แก้แล้วด้วย
    `MaxApiKeyLength = 200` validate ก่อนแตะ protector/DB)
  - **backlog ไม่บล็อก 2 ข้อจาก security review**: (1) Development ที่ไม่มี cert ควร log คำเตือนให้
    ชัดกว่า default ของ framework (nice-to-have ไม่บังคับ) (2) **ต้องใส่ rate limit ก่อน Part 2
    เพิ่ม live validation** — ไม่งั้น endpoint นี้จะกลายเป็น oracle เช็คว่า stolen API key ยังใช้ได้
    ไหมผ่านบัญชีที่ compromised (ยังไม่ exploitable วันนี้เพราะยังไม่มี live call ไปหา provider จริง)
  - ทดสอบจริง 148/148 ผ่าน (18 ใหม่ ใน `ApiKeyCrudTests.cs` รวม encrypt/decrypt round-trip ข้าม
    `WebApplicationFactory` สองตัวแยกอิสระผ่าน Redis จริง, concurrent-race ผ่าน `Task.WhenAll` จริง,
    multi-tenant uniqueness, cross-user RLS isolation) รวม **369 เทสต์ทั้ง repo**
- **Part 2** ✅ **เสร็จ 2026-08-27** — Tool-calling loop skeleton: `POST
  /campaigns/{id}/sessions/{id}/consult` เรียก Anthropic API จริงด้วยคีย์ BYOK ของ DM (Part 1) มี
  tool เดียว (`roll_dice`) พิสูจน์ narration → tool_use → tool_result → narration จบ loop ได้จริง
  ยังไม่มี approval gate (auto-allow เสมอ) + เติม "validate ด้วย test call" ที่เลื่อนมาจาก Part 1
  กลับเข้า `PUT /users/me/api-keys/{provider}` แล้ว
  - ⚠️ ~~งานนี้ทำเองทั้งหมดโดย main ไม่ผ่านการรีวิวอิสระ~~ **แก้แล้ว — ผ่าน retroactive review
    ครบแล้ว 2026-08-27** (tier agent unavailable ตอนพัฒนาเริ่มแรก ตอนนั้น main ทำเองพร้อม
    self-review ละเอียดกว่าปกติ พอ agent กลับมาใช้งานได้จริง ผู้ใช้สั่งให้ส่งย้อนหลังทันที — ดูผล
    retroactive review รวม Part 2+3 ที่ท้าย Part 3 ด้านล่าง)
  - สถาปัตยกรรมหลัก: `IAnthropicClient` แยก interface ออกจาก implementation จริง (`AnthropicClient`
    เรียก Messages API ตรงผ่าน `HttpClient` ไม่ใช้ SDK) — เพราะเป็น external paid API เรียกจริงใน
    เทสต์อัตโนมัติไม่ได้เลย (ไม่มี key จริง/ไม่ deterministic/มีต้นทุนจริง) ทุกเทสต์สลับเป็น
    `FakeAnthropicClient` แทน (pattern มาตรฐานสำหรับ external HTTP dependency ต่างจาก "ห้าม mock"
    ที่ใช้กับกลไกภายในระบบนี้เอง เช่น dice/Postgres/Redis)
  - **จำกัดผู้เรียกไว้แค่ DM (campaign owner) เท่านั้น** — ไม่ใช่แค่ product design
    ("quick play ใช้ key ของ DM เท่านั้น") แต่เป็นเหตุผลทาง RLS ล้วน ๆ: `own_rows_only` ของ
    `api_keys` กรองด้วย `app.user_id` ของ**ผู้เรียก**เท่านั้น ถ้าเปิดให้ player เรียกได้ query หา
    "key ของ DM" จะโดน RLS กรองว่างเปล่าเสมอ (เว้นแต่ทำ `SECURITY DEFINER` function แบบ
    `app_create_campaign()` ซึ่งเกินขอบเขต skeleton) — สมาชิกที่ไม่ใช่ DM ได้ 403 (ครั้งแรกในระบบนี้
    ที่แยก 403 จาก 404 ชัดเจน: 404=มองไม่เห็นเลย, 403=เห็นแต่ทำ action นี้ไม่ได้)
  - **จำกัด 1 รอบ tool round-trip เท่านั้น** — ยังไม่ใช่ agentic loop เต็มรูปที่เรียก tool ต่อเนื่องได้
  - persist ทั้งข้อความ DM (`Message`, `AuthorType=Dm`, `Source=DmOverride`) และ narration ของ AI
    (`AuthorType=Ai`, `Source=Ai`, `ToolCalls` jsonb ถ้ามี) ลงตาราง `messages` ที่มีอยู่แล้วตั้งแต่
    initial schema แต่ไม่เคยมี endpoint ต่อจริงมาก่อน (เหมือน `api_keys` ตอน Part 1) — คราวนี้ตรวจ
    แล้วไม่มีช่องโหว่แบบเดียวกัน (constraint/RLS ครบอยู่แล้ว)
  - roll_dice ที่ AI เรียก **persist เป็น `dice_rolls` แถวจริงเสมอ** ตามข้อบังคับ PROJECT-CONTEXT §3
    (`CharacterId=null` เพราะเป็นการทอยของ AI/DM เอง ไม่ผูก character) seed ไม่โผล่ใน tool_result/
    ToolCalls jsonb/response เด็ดขาด (เหมือนกฎเดิมทุกจุดที่แตะ DiceEngine)
  - **ช่องโหว่จริงที่เจอเองระหว่าง self-review**: `ExecuteRollDice` เดิมจับแค่ `KeyNotFoundException`
    (ไม่มี field "expression") แต่ไม่ครอบ `InvalidOperationException` (input ไม่ใช่ JSON object เลย/
    "expression" ไม่ใช่ string) — tool input ผิดรูปแบบแบบหลังจะ throw ขึ้นไปจน request ทั้งก้อนล้ม
    500 แทนที่จะกลายเป็น tool_result error ตามที่ตั้งใจ แก้แล้ว + เพิ่มเทสต์ยืนยัน
  - **backlog ที่รู้ตัวแต่ไม่แก้ตอนนี้** (ตรงกับ pattern "skeleton ไม่ต้องมี resilience เต็มรูป"):
    ถ้า Anthropic API คืน error (5xx/429/network) ระหว่าง `SendTurnAsync`/`ContinueWithToolResultAsync`
    (เปลี่ยนชื่อจาก `SendWithSingleToolRoundTripAsync` เดิมตอน Part 3 — ดูด้านล่าง) จะ throw
    `AnthropicApiException` ที่ไม่มีใครจับ กลายเป็น 500 ดิบ — ยังไม่มี retry/circuit-breaker pattern
    ไหนในระบบนี้เลยตอนนี้ (สอดคล้องกับที่ยังไม่ต้องมี pattern แบบนั้นที่จุดอื่นเช่นกัน)
  - ทดสอบจริง 162/162 ผ่าน (148 เดิม + 4 ใหม่ใน `ApiKeyCrudTests.cs` ครอบ validation-outcome
    ทั้งสามกรณี + 10 ใหม่ใน `ConsultEndpointsTests.cs`) รวม **383 เทสต์ทั้ง repo** — ยืนยันเสถียร
    2 รอบติดกัน (concurrent-race test ของ Part 1 รวมอยู่ด้วย)
- **Part 3** ✅ **เสร็จ 2026-08-27 — 🎉 Sprint D ครบ 3 part แล้ว** — Approval gate ผูกกับ
  `AutonomySetting.Subsystem=RulesCombat` ของแคมเปญ: `Manual` = ไม่เสนอ tool ให้ Claude เลย,
  `Suggest` (default เมื่อไม่มีแถวตั้งค่า — ทุกแคมเปญตอนนี้) = สร้าง `Proposal` (Pending) รอ DM
  resolve เอง ไม่ execute ทันที, `Autonomous` = execute ทันทีเหมือน Part 2 เดิม — เพิ่ม
  `POST /campaigns/{id}/proposals/{id}/resolve` ให้ DM approve/reject
  - ⚠️ ~~งานนี้ทำเองทั้งหมดโดย main ไม่ผ่านการรีวิวอิสระเหมือน Part 2~~ — tier agent unavailable
    ตอนพัฒนา (ทดสอบ diagnostic ping ยืนยันแล้วตอนนั้น) ใช้วินัยของ tier เท่าที่ทำเองได้แทน (decision
    framework, TDD-first, self-review, log ทุกจุดตัดสินใจ) **แก้แล้ว — ผ่าน retroactive review
    ครบแล้ว 2026-08-27** ทันทีที่ tier agent กลับมาใช้งานได้ (ดูผลด้านล่าง)
  - **ต้องปรับ `IAnthropicClient` interface ใหม่ทั้งหมด** — Part 2 เดิมมี `SendWithSingleToolRoundTripAsync`
    ตัวเดียวที่ auto-execute tool ในตัว (ผ่าน callback) ใช้กับ `Suggest` ไม่ได้เลยเพราะต้อง**หยุดรอ
    ระหว่าง** "รู้ว่า Claude อยากเรียก tool" กับ "tool ถูก execute จริง" (อาจข้าม HTTP request เป็นนาที/
    ชั่วโมงจนกว่า DM จะกด approve) — แยกเป็น `SendTurnAsync` (ไม่ execute อะไรเองเลย) กับ
    `ContinueWithToolResultAsync` (ส่งต่อหลัง execute แล้วจริง) แทน ทั้งสองใช้ `JsonElement` ธรรมดา
    (ไม่ใช่ `JsonNode`/object ทึบ) สำหรับ raw assistant content เพราะต้อง**persist ข้าม HTTP request
    ได้จริง** (เก็บใน `Proposal.ToolCallJson` ระหว่างรอ approve)
  - **ห้ามทอยดักไว้ล่วงหน้าตอนสร้าง Proposal เด็ดขาด** — `Suggest` path เก็บแค่ tool call ที่ Claude
    "อยากทำ" (name+input+raw content+history ทั้งหมด) ไม่เรียก `DiceEngine` เลยจนกว่า DM จะ approve
    จริง ไม่งั้นผลจะรั่วให้เห็นก่อน approve ซึ่งขัดกับความหมายทั้งหมดของการ "รอ approve" — ยืนยันด้วย
    เทสต์ตรง ๆ ว่าไม่มี `DiceRoll` แถวใหม่เกิดขึ้นเลยตอนสร้าง Proposal
  - `Proposal.ToolCallJson` เก็บ payload ที่ self-contained ครบ (tool name/input/id, raw assistant
    content, **ประวัติบทสนทนาทั้งหมด ณ ตอนสร้าง**) แทนที่จะพึ่งการ query `messages` ใหม่ตอน resolve —
    กัน correctness drift ถ้ามีข้อความอื่นแทรกเข้ามาระหว่างรอ DM ตัดสินใจ
  - broadcast `ProposalCreated` เข้า SignalR group ของแคมเปญ (Sprint C) แจ้ง DM console — wrap
    try/catch log-and-swallow ตั้งแต่ต้น (ใช้ pattern ที่แก้ไว้แล้วใน Sprint C Part 3 ไม่ต้องรอเจอ
    บั๊กซ้ำ)
  - **เก็บไว้นอกขอบเขตตั้งใจ**: ยังไม่มี endpoint ให้ DM ตั้งค่า/แก้ไข `AutonomySetting` เอง (Part 3
    ทำแค่ฝั่ง**อ่าน/บังคับใช้** dial ไม่ใช่ฝั่งจัดการ) — ตอนนี้ทุกแคมเปญจริงในระบบยังเปลี่ยนจาก
    default `Suggest` ไม่ได้เลยนอกจากแก้ตรง DB (เทสต์ seed ผ่าน raw SQL) การจัดการ dial ผ่าน DM
    console เป็นงานแยกที่ยังไม่ได้ทำ
  - ทดสอบจริง 172/172 ผ่าน (162 เดิม + 10 ใหม่ใน `ConsultEndpointsTests.cs` ครอบ Manual/Suggest/
    Autonomous/resolve-approve/resolve-reject/409-conflict/403-vs-404/malformed-input-ผ่าน-resolve/
    SignalR broadcast) รวม **393 เทสต์ทั้ง repo** — ยืนยันเสถียร 2 รอบติดกัน

**Retroactive review — Sprint D Part 2+3 (2026-08-27)** ทันทีที่ tier agent กลับมาใช้งานได้
(หลังปิด-เปิด desktop app ใหม่) ผู้ใช้สั่งให้ส่งงานที่ทำไปตอน agent unavailable เข้ารีวิวย้อนหลัง —
dispatch code-reviewer + security-reviewer พร้อมกันตรวจ current state ของโค้ดทั้งก้อน (ไม่แยก
Part 2/3 เพราะ Part 3 rewrite `IAnthropicClient` ของ Part 2 ทับเกือบหมดแล้ว)

- **Critical เจอจริง 1 จุด (ยืนยันอิสระโดยทั้งสอง reviewer)**: `PUT /users/me/api-keys/{provider}`
  เรียก live validation ไปหา Anthropic จริงทุกครั้งโดยไม่มี rate limit เลย ทั้งที่ security review
  ของ **Part 1 เองล็อกไว้แล้วว่าต้องมี rate limit ก่อน Part 2 เพิ่ม live validation** — Part 2
  shipped ไปโดยไม่ทำตามเงื่อนไขนั้น กลายเป็น oracle ให้ผู้ใช้ที่ authenticate แล้วเช็คได้ว่า string
  ใด ๆ เป็น Anthropic API key ของคนอื่นที่ขโมยมาใช้ได้จริงไหม — **แก้แล้ว**: เพิ่ม rate limiting
  (ASP.NET Core built-in `Microsoft.AspNetCore.RateLimiting` ไม่ต้องเพิ่ม NuGet) partition ตาม
  user id, 5 request/นาที, `RejectionStatusCode=429`, วาง `UseRateLimiter()` ก่อน RLS middleware
  (กัน request ที่โดน limit ไปเปลืองคอนเนกชัน DB) ยืนยันด้วยเทสต์จริง (6 request ต่อเนื่อง ตัวที่ 6
  ได้ 429)
- **Warning เจอเพิ่มโดย code-reviewer**: TOCTOU race บน `POST /campaigns/{id}/proposals/{id}/resolve`
  — เดิม read-then-write เช็ค `proposal.Status` ไม่ atomic เลย สอง concurrent resolve อาจ execute
  tool ซ้ำสองรอบ (double dice roll + double Anthropic call) — **แก้แล้ว**: เปลี่ยนเป็น
  `db.Proposals.Where(p => p.Id == id && p.Status == Pending).ExecuteUpdateAsync(...)` (EF Core
  bulk update, atomic ที่ระดับ Postgres row lock จริง) claim ก่อน execute อะไรเลยทั้ง
  approve/reject branch ยืนยันด้วยเทสต์ concurrent จริงผ่าน `Task.WhenAll` (ผู้ชนะ 1 ราย 200, แพ้
  1 ราย 409, tool execute ครั้งเดียว) — เสถียร 3 รอบติดกัน (deterministic เพราะพึ่ง DB lock จริง
  ไม่ใช่ race แบบสุ่ม)
- **ยืนยันถูกต้องแล้ว (ไม่ต้องแก้)**: IDOR บน resolve endpoint เป็นไปไม่ได้ (RLS + explicit
  cross-campaign check), Suggest mode ไม่ roll ลูกเต๋าล่วงหน้าจริง (ยืนยันจากอ่าน code path ไม่ใช่
  แค่เชื่อเทสต์), `Proposal.ToolCallJson` ที่เก็บ conversation history ระหว่างรอ approve ได้รับ RLS
  คุ้มครองเท่าตารางอื่นในกลุ่มเดียวกัน (`messages`/`dice_rolls`)
- **Warning ที่เหลือเป็น backlog** (ไม่ใช่ regression เหมือน 2 ข้อบน — เป็นข้อสังเกตใหม่ที่ต้อง
  ออกแบบเพิ่ม ไม่ใช่แค่แพตช์): ไม่มี cost-throttle บน `/consult` เอง (ส่ง history ทั้งก้อนทุกครั้ง
  ไม่มี window/cap, ไม่มี token-counting UI ตามที่ PROJECT-CONTEXT §3 วางแผนไว้ — stolen DM JWT
  ทำให้เกิดความเสียหายทางการเงินจริงกับบัญชี Anthropic ของ DM ได้ไม่จำกัด) — รอออกแบบร่วมกับ
  Sprint E ตอนมี UI จริงให้โชว์ค่าใช้จ่าย
- ผ่าน code review 2 รอบ (รอบ 2 ตรวจเฉพาะ diff ของ fix ทั้งสอง approve ไม่มีปัญหาใหม่) และ
  security review 1 รอบ (ไม่ escalate opus-worker ทั้งคู่ — evidence ครบ ไม่ใช่ปัญหา crypto/session
  ซับซ้อนที่ต้องใช้ผู้เชี่ยวชาญระดับสูงกว่าตัดสิน) — ทดสอบจริง 395/395 ผ่าน (221 Rules + 174 API,
  2 เทสต์ใหม่จากรอบรีวิวนี้) เสถียร 3 รอบติดกัน

**Sprint E — Frontend MVP** (2026-08-27) แบ่ง 3 part: Part 1 scaffold+auth/SignalR proof →
Part 2 DM console → Part 3 player action input

- **Part 1** ✅ **เสร็จ 2026-08-27** — Next.js 16 (App Router, TS, Tailwind v4, Turbopack) scaffold
  ที่ `frontend/` (root-level ไม่ใช่ `src/` เพราะ `src/*` ทั้งหมดเป็น .csproj ใน `DnDEz.sln`) + PWA
  config (manifest.json + icon 192/512/apple-touch-icon, ไม่มี service worker เพราะโปรเจกต์ล็อกไว้
  "ไม่ทำ offline mode" — PWA scope นี้ = installability เท่านั้น ไม่ใช่ offline caching) + auth
  (login/register ต่อ `/auth/*` จริง) + SignalR connection proof (join `CampaignHub` group จริง)
  — ทำเองตรง ๆ ไม่ dispatch แม้มี `<tier>` (decision framework exception, บันทึกเหตุผลเต็มใน
  `.claude/logs/routing.md`): ตำแหน่ง frontend ในโครงสร้าง repo, เปิด CORS ฝั่ง API (ไม่เคยมีมาก่อน
  เลย — backend ทดสอบผ่าน `WebApplicationFactory` ตรงมาตลอดไม่เคยผ่าน browser), JWT storage
  ฝั่ง frontend
  - **CORS**: เพิ่ม policy ใน `Program.cs` (`WithOrigins` จาก env `DNDEZ_FRONTEND_ORIGIN` default
    `http://localhost:3001`) — ไม่เปิด `AllowCredentials` เพราะ auth ทั้งระบบใช้ Bearer token
    (Authorization header / query string เฉพาะ SignalR hub) ไม่ใช่ cookie เลย — 174/174 API เทสต์
    เดิมยังผ่านหลังเพิ่ม (CORS middleware ไม่กระทบ request ที่ไม่มี `Origin` header เหมือนที่
    `WebApplicationFactory` ส่ง)
  - **JWT storage**: เลือก `localStorage` (ผ่าน `zustand/middleware persist`) ไม่ใช่ in-memory-only
    เพราะ backend ยังไม่มี refresh-token mechanism เลย (Sprint A Part 2 บันทึก backlog ไว้แล้ว) การ
    persist ข้าม page refresh จำเป็นต่อการใช้งานจริงตอนนี้ — ยอมรับความเสี่ยง XSS-exfiltration เป็น
    known tradeoff เดียวกับที่ backend ยอมรับเรื่อง JWT ไม่มี revocation อยู่แล้ว (backlog ก่อน
    production เหมือนจุดอื่น)
  - **frontend state stack ตาม PROJECT-CONTEXT §3 ที่ล็อกไว้**: TanStack Query สำหรับ server state
    (campaigns/sessions list + mutations) + Zustand สำหรับ UI state (selected campaign/session, hub
    status, event log) + `@microsoft/signalr` ตรงเป๊ะ — **เจอเหตุผลเพิ่มเติมที่ยืนยันว่าตัวเลือกนี้ถูก
    จริง ไม่ใช่แค่ตามสเปก**: raw `useState`+`useEffect` fetch pattern ชน ESLint rule ใหม่ของ
    `eslint-plugin-react-hooks` (`react-hooks/set-state-in-effect`, มากับ Next.js 16/React 19.2) ที่
    ตรวจจับ dataflow ของ setState ที่ถูกเรียกผ่าน effect แม้ทางอ้อม (ผ่าน `useCallback` ที่ถูกเรียกใน
    effect) — ย้ายไป TanStack Query/Zustand แก้ปัญหานี้ได้ตรง ๆ เพราะ setState เกิดใน library internals
    ไม่ใช่ effect ของ component เอง (`npx tsc --noEmit` + `npm run lint` ผ่านสะอาดหลังย้าย)
  - **SignalR + React StrictMode (dev only)**: StrictMode รัน effect mount→cleanup→mount ทันที ทำให้
    connection ตัวแรกถูก `stop()` กลางอากาศตอน negotiate — เพิ่ม `cancelled` flag guard ใน effect กัน
    callback ของ connection ที่ถูก supersede แล้วมาเขียนทับ state ของ connection ตัวที่สองที่เชื่อมสำเร็จ
    จริง (คลาสเดียวกับ pattern ที่ใช้ใน join-group effect อยู่แล้ว) — เหลือ console.error ของไลบรารี
    เองที่ไม่กระทบ state (เห็นเฉพาะ dev, StrictMode ไม่ทำงานใน production build) ยอมรับเป็น cosmetic
    noise ไม่ fix เพิ่มเพราะไม่คุ้มความซับซ้อนของการเขียน custom logger มา filter สาย log เดียว
  - **ทดสอบจริงผ่าน browser จริง** (Next.js dev server :3001 + API :5080 + Postgres/Redis จริงผ่าน
    docker compose ของ worktree `dndez-continuation-26872c` ที่รันอยู่แล้ว — พอร์ตมาตรฐาน 5432/6379
    ใช้ร่วมกันได้เพราะเป็น physical DB เดียวกัน ไม่ต้องแยก stack ต่อ worktree): สมัคร/login →
    สร้างแคมเปญ (`app_create_campaign()` ผ่าน CORS) → สร้างเซสชัน → join SignalR group จริง → ทอย
    ลูกเต๋าจริงผ่าน `DiceEngine` → เห็นทั้ง REST response และ realtime `DiceRolled` broadcast
    ยืนยันผลตรงกัน (proof ครบ auth+REST+rules engine+DB+SignalR ในรอบเดียว) → logout เคลียร์ state
    + redirect ถูกต้อง
  - sonnet-worker ping diagnostic สำเร็จก่อนเริ่ม (tier agent พร้อมใช้งานตลอด Part นี้)
  - ผ่าน **code review 2 รอบ** — รอบ 1 เจอบั๊กจริง: `@microsoft/signalr` reconnect เปิด
    `ConnectionId` ใหม่ แต่ server-side group membership (`Groups.AddToGroupAsync`) ผูกกับ
    `ConnectionId` เดิม ทำให้ broadcast เงียบหายหลัง network blip โดย UI ยังโชว์ "connected" — แก้
    ด้วยเคลียร์ `joinedCampaignId` ใน `onreconnecting`/`onclose` ให้ join effect เดิม re-invoke
    `JoinCampaign` เอง (commit `90424a6`) ยืนยันด้วยการ kill+restart API process จริงระหว่าง
    session แล้วดู log ว่า join group เกิดซ้ำ + ทอยลูกเต๋าหลัง reconnect ยังได้ broadcast กลับมา —
    รอบ 2 approve ไม่มี finding ใหม่ ปิดรอบ (cap 2 รอบตาม CLAUDE.md)
  - ผ่าน **security review** — clean ไม่มี Critical/Warning (`npm audit` 0 vulnerabilities, CORS
    ปลอดภัยเพราะไม่มี cookie ในระบบเลยทั้ง frontend/backend, ไม่พบ XSS surface — ทุกจุดที่ render
    ข้อมูลจากผู้ใช้ผ่าน JSX interpolation ล้วน ไม่มี `dangerouslySetInnerHTML`) มี backlog ไม่บล็อก
    2 ข้อ: (1) scrub JWT ออกจาก proxy access log ของ `/hubs/campaign` ตอนมี deployment target จริง
    (2) ทบทวน JWT storage (`localStorage`) อีกครั้งตอนมี refresh-token mechanism
  - **✅ เงื่อนไขก่อนเริ่ม Part 2 ทำแล้ว (2026-08-27)** — ตั้ง Vitest + React Testing Library
    (`vitest.config.mts`, `vitest.setup.ts`, script `npm run test`) และ **แตก SignalR connection
    lifecycle + rejoin-on-reconnect ออกจาก `CampaignConsole.tsx` เป็น
    `src/lib/campaign-hub-manager.ts`** (plain TS class ไม่มี dependency กับ React, รับ
    `HubConnectionLike` interface แทน `signalR.HubConnection` ตรง ๆ เพื่อ inject fake connection
    ในเทสต์ได้โดยไม่ต้องมี WebSocket จริง) — `CampaignConsole.tsx` เหลือแค่เรียกผ่าน hook บาง ๆ
    `use-campaign-hub.ts`
    - เขียน regression test ของบั๊ก reconnect โดยเฉพาะ (`campaign-hub-manager.test.ts`) —
      **ยืนยันว่าเทสต์จับบั๊กได้จริง** ด้วยการลบ `this.queueSync()` ออกจาก `onreconnected` ชั่วคราว
      (จำลองบั๊กเดิม) แล้วรัน `npm run test` เห็น test นี้ fail ตรงจุดเดียวที่ควร แล้วค่อย revert กลับ —
      ไม่ใช่แค่เขียนเทสต์แล้วเชื่อว่าผ่าน
    - เก็บ `syncChain` (promise chain serialize การ join/leave) แก้ theoretical race ที่
      code-reviewer เคยตั้งข้อสังเกตไว้ตอน round 1 (ไม่บล็อกตอนนั้น) ไปด้วยในตัว เพราะ manager ใหม่
      re-read state ล่าสุดทุกครั้งก่อน sync แทนการพึ่ง `cancelled` flag ต่อ effect เหมือนโค้ดเดิม
    - เพิ่มเทสต์ `console-store.test.ts` คู่กัน (log cap ที่ 50 แถว, เลือกแคมเปญใหม่ล้าง session)
    - 9/9 เทสต์ผ่าน + `tsc --noEmit`/`lint`/`build` สะอาด + ทดสอบซ้ำผ่าน browser จริงยืนยันพฤติกรรม
      ไม่เปลี่ยนหลัง refactor (join/roll dice ยังทำงานถูกต้อง)
- **Part 2** — DM console แบบเต็ม (proposal queue/resolve, approval gate UI) — ยังไม่เริ่ม
- **Part 3** — player action input แบบ hybrid + HP realtime — ยังไม่เริ่ม

**ทำคู่ขนานได้ตลอด ไม่บล็อกอะไร**: ตั้ง GitHub Actions CI ตั้งแต่ Sprint A เริ่ม (ต้นทุนแทบศูนย์)

**เลื่อนไปหลัง pilot ได้** (ไม่บล็อก MVP): source import pipeline (ต้องตัดสินใจ embedding provider
ก่อนด้วย — ดูหัวข้อด้านล่าง), VPS staging + Jenkins CD (ทำตอนมีอะไรให้ deploy จริงแล้ว)

**ฝั่ง Player — ออกแบบรอบแรกเสร็จแล้ว** (2026-08-21)
คำถามเปิดเดิม "แท็บเล็ตผู้เล่นดู sheet อย่างเดียวหรือประกาศ action ได้ด้วย" → **ตอบแล้ว: ประกาศ action ได้**
เหลือ 2 จุดที่รอ feedback จากการใช้จริง
- ภาระ DM ที่ต้องกรอกผลเต๋าให้ผู้เล่นทุกคนในโหมด in-person (`player-side-design.md` §5)
- การส่ง panel แนะนำ action ในโหมด online ที่ DM ไม่ได้พูดสด (`player-side-design.md` §7)

**⚠️ Embedding provider — ต้องตัดสินใจก่อนเปิดฟีเจอร์ source import** (เจอ 2026-08-21 ตอนลง schema)
`raw_chunks` ต้องมี vector สำหรับค้นหาเชิงความหมาย แต่ **Anthropic ไม่มี embeddings API**
ต้องใช้ provider เจ้าอื่น (Voyage / OpenAI / รันโมเดล open-source เอง)
→ **แนะนำรันเอง** เพราะเนื้อหาหนังสือที่ DM สแกนเป็นของมีลิขสิทธิ์ + private ต่อแคมเปญ
  การส่งออกไปให้เจ้าที่สามขัดกับกฎที่ล็อกไว้ และไม่ต้องยุ่งกับ key ที่สอง

**ไม่ใช่ปัญหาเชิงโครงสร้าง** — กระทบแค่คอลัมน์ `raw_chunks.embedding` คอลัมน์เดียวกับโค้ดจุดเดียว
เพราะ embedding เป็น**ข้อมูลที่คำนวณใหม่ได้เสมอ**จาก `raw_chunks.content` ที่เก็บไว้อยู่แล้ว
เปลี่ยน provider ทีหลัง = แก้มิติคอลัมน์ + รัน job คำนวณใหม่ ไม่ต้องสแกนหนังสือใหม่ ไม่แตะ 29 ตารางที่เหลือ
(ที่ทำได้แบบนี้เพราะเลือก pgvector ไว้ — ถ้าใช้ vector DB แยกจะต้อง sync ข้ามระบบด้วย)

**ยังไม่ออกแบบ**: หน้าจอ onboarding/ตั้งค่าแรกเข้า · flow การจ่ายเงิน/แพ็กเกจ storage ·
รายละเอียด UI ของ table view

**เก็บไว้ phase ถัดไป**
- **Image generation — ตัดออกจาก phase แรกแล้ว (ตัดสิน 2026-08-21)**
  `dm-side-design.md` §2.1 มี tool `generate_scene_image` และ §4.2 บอกว่ากด generate แผนที่ด้วย AI ได้
  แต่ **Claude API สร้างรูปภาพไม่ได้** ต้องพึ่ง provider เจ้าอื่น ซึ่งทำให้ BYOK ซับซ้อนขึ้นทันที
  → phase แรกใช้**แผนที่ที่ DM อัปโหลดเอง + คลัง asset สำเร็จรูป** ก็เพียงพอ ไม่กระทบโครงหลัก
- Fully-homebrew mode (ออกนอกกรอบ D&D 2024/SRD ทั้งหมด)
  → ข้อควรระวังที่คุยไว้: ถ้า homebrew เปลี่ยนแกน d20/AC/HP → rules engine ใช้ไม่ได้
    แนะนำจำกัดให้ใช้แกน d20 เสมอ เปลี่ยนได้แค่เนื้อหา
- XP-based leveling (รองรับใน data model แต่ยังไม่ build)
- AI ช่วยผู้เล่นหา feat/spell/item + auto-generate build
- Pause/resume vote จากฝั่งผู้เล่น

---

## 5. วิธีใช้เอกสารนี้ในแชทใหม่

**สำหรับแชทออกแบบต่อ**: อ่านไฟล์นี้ + `dm-side-design.md` + `player-side-design.md`
แล้วบอกว่าจะออกแบบส่วนไหนต่อ

**สำหรับ Claude Code / งานเขียนโค้ด**: ยึด §2 (การตัดสินใจที่ล็อก) กับ §3 (stack) เป็นข้อบังคับ
data model เต็มอยู่ใน `dm-side-design.md` §3 + ส่วนที่เพิ่มจากฝั่งผู้เล่นใน `player-side-design.md` §23

**เมื่อมีการตัดสินใจใหม่**: อัปเดตไฟล์นี้ (§2 หรือ §4) แล้ว commit ทุกครั้ง
เพื่อให้ทุกแชทเห็นสถานะเดียวกัน — ไฟล์นี้คือ single source of truth ของบริบทโปรเจกต์

# DnDEz — Project context (handoff)

> เอกสารนี้คือ **primer สำหรับเริ่มแชทใหม่** (Cowork, Claude Code, หรือแชทออกแบบต่อ)
> อ่านไฟล์นี้ก่อน แล้วค่อยเปิด [`dm-side-design.md`](./dm-side-design.md) /
> [`player-side-design.md`](./player-side-design.md) ถ้าต้องการรายละเอียดเต็ม
>
> **สถานะ**: ฝั่ง DM ออกแบบเสร็จแล้ว · ฝั่ง Player ออกแบบรอบแรกเสร็จแล้ว (2026-08-21)
> · **DB schema + RLS ลงโค้ดแล้ว** (30 ตาราง — ดู [`db-schema.md`](./db-schema.md)) · ยังไม่มี API/frontend
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
| **3** | death save state machine · action economy (action/bonus/reaction/movement) | ⏳ ยังไม่เริ่ม |

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

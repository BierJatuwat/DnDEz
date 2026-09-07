# Sprint G — prompt สำเร็จรูปต่อ Part

> **สถานะ**: เขียน 2026-09-06 · ยังไม่มี Part ไหนเริ่มทำ
>
> ใช้คู่กับ [`PROJECT-CONTEXT.md`](./PROJECT-CONTEXT.md) §4 "Sprint G" (ขอบเขตของแต่ละ Part)
> และ [`spatial-play-design.md`](./spatial-play-design.md) (การตัดสินใจเชิงออกแบบ + กลไกป้องกัน §19–22)
>
> ⚠️ **prompt ของ Part 5–10 เขียนบนสมมติฐานว่า Part ก่อนหน้าลงตามแผน** — ก่อนใช้จริงต้อง
> ตรวจซ้ำว่าชื่อไฟล์/ตาราง/endpoint ที่อ้างถึงยังตรงกับของจริงไหม อย่าลอกไปใช้ทั้งดุ้นโดยไม่ดู

---

## วิธีใช้

1. คัดลอก prompt ของ Part นั้นไปวางในแชทใหม่ (แชทละ 1 Part — กันชน token limit)
2. ถ้าหัวข้อบอกว่า **ใช้ tier** ให้ขึ้นต้นข้อความด้วย `<tier>` · ถ้าบอกว่า **ไม่ใช้** ห้ามใส่
3. ถ้าไม่ใช้ tier ให้สลับโมเดลตามที่ระบุก่อนเริ่ม (`/model claude-opus-5` หรือ `claude-sonnet-5`)

## ข้อเท็จจริงที่ทุก Part ใช้ร่วมกัน (ตรวจแล้ว 2026-09-05/06)

**คำสั่งทดสอบ**
- backend: `dotnet test tests/DnDEz.Api.Tests/DnDEz.Api.Tests.csproj`
- frontend: `npx tsc --noEmit` · `npm run lint` · `npm run test` · `npm run build` (ใน `frontend/`)
- รัน API: `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DnDEz.Api --urls http://localhost:5080`
  (ถ้าไม่ตั้ง `ASPNETCORE_ENVIRONMENT=Development` จะ throw เรื่อง `DNDEZ_DATA_PROTECTION_CERT_PATH`)
- รัน frontend: ใช้ preview config ชื่อ `frontend-dev` ใน `.claude/launch.json` (port 3001)
- ต้องปิด API ก่อนรัน `dotnet test` ไม่งั้น build ล็อกไฟล์ dll

**เทสต์ที่ล้มอยู่แล้วก่อนเริ่ม (ไม่ใช่ของที่ทำพัง)**
`ApiKeyCrudTests.Put_api_key_when_validation_outcome_is_unknown_preserves_existing_status_on_rotate`
— ปัญหา precision ของ timestamp ยืนยันด้วยการ stash โค้ดใหม่ออกแล้วยังล้มเหมือนเดิม

**กับดักที่เจอมาแล้วจริง — ห้ามพลาดซ้ำ**
1. `dotnet ef migrations add` จะ generate `AddColumn<uint>(name: "xmin", ...)` ผิด ๆ (bug ของ
   Npgsql scaffolder, npgsql/efcore.pg#3854) — **ต้องลบบรรทัด xmin ออกจากไฟล์ migration ด้วยมือ**
   แต่ห้ามแตะ config ใน DbContext
2. **ตารางใหม่ที่ผูกกับแคมเปญ fail OPEN** — RLS loop ใน migration `RowLevelSecurity` (2026-08-21)
   รันครั้งเดียวตอนนั้น ไม่ใช่ trigger ถาวร ทุก migration ที่สร้างตารางใหม่ต้องเขียน
   `ENABLE ROW LEVEL SECURITY` + `FORCE ROW LEVEL SECURITY` + `CREATE POLICY` เอง
   และเพิ่มเทสต์ยืนยัน `relrowsecurity`/`relforcerowsecurity` (ดูตัวอย่าง `EncounterRlsTests.cs`)
3. **route ต้องมี `{campaignId}` เสมอ** — middleware ที่ `Program.cs:276` อ่าน campaign จาก route
   value แล้ว policy เทียบกับ `app_current_campaign()` ซึ่งคืน NULL ถ้าไม่ได้ตั้ง →
   `campaign_id = NULL` → **กรองทุกแถวทิ้งแบบเงียบ ๆ** (ไม่ error แต่ไม่เห็นข้อมูลอะไรเลย)
4. **SignalR hub เขียน state ไม่ได้** ด้วยเหตุผลเดียวกับข้อ 3 (invocation ไม่มี route value)
   hub ปัจจุบันมีแค่ `JoinCampaign`/`LeaveCampaign` — ห้ามเพิ่ม method ที่เขียน DB

**Definition of Done ของทุก Part (บังคับ — `spatial-play-design.md §20`)**
- **(ก)** vertical slice ถึงจอจริง · ถ้าเป็น Part ฐานข้อมูลล้วนต้องเขียนกำกับว่า "ยังเล่นไม่ได้"
- **(ข)** เขียนคำตอบของ *"คนไม่รู้กฎ D&D เจอหน้านี้แล้วรู้ว่าต้องทำอะไรไหม โดยไม่ต้องพิมพ์ค่าที่ไม่รู้"*
  ลงในสรุปงานเป็นข้อความ ไม่ใช่แค่คิดในหัว
- **(ค)** `grep` endpoint เทียบตารางที่แตะ ยืนยันไม่มีตารางกำพร้า บันทึกผลไว้
- **(ง)** ห้ามสลับลำดับ Part เอง

**⚠️ ของใน mockup ที่ "ไม่อยู่ในขอบเขต Sprint G"** — mockup จาก UXPilot มีฟีเจอร์ที่ไม่มีทั้งใน
แผนและ backend: `Voice to DM`, `AI DM — โทนเรื่อง (GRIMDARK/HEROIC/...)`, การ์ด spawn NPC จาก
คลัง statblock พร้อม CR · **เห็นในภาพแล้วห้ามทำตาม** ถ้าจะทำต้องคุยเพิ่มขอบเขตก่อน

---

## Part 1 — คลัง SRD + ability scores

| | |
|---|---|
| **tier** | ✅ ใช้ — สร้างไฟล์ใหม่หลายไฟล์ + แตกงานได้ (migration / entity / seed) → dispatch ตรงไป `opus-worker` ข้าม first triage ตามข้อ 1a |
| **ถ้าไม่ใช้ tier** | opus |
| **security review** | ✅ ต้องมี (migration + การตัดสินใจเรื่อง RLS ของตารางระดับระบบ) |

```
<tier> Sprint G Part 1 — สร้างคลังกลไก (action catalog) + เพิ่ม ability scores ใน Character

อ่านก่อน: docs/spatial-play-design.md §2 (คลังกลไก) และ §2.5 (ability scores หายไป)
+ docs/PROJECT-CONTEXT.md §4 หัวข้อ "Sprint G" Part 1

⚠️ Part นี้เป็นฐานข้อมูลล้วน ยังเล่นไม่ได้จนกว่าจะถึง Part 7/9 — ให้เขียนกำกับไว้ในสรุปงาน
ห้ามให้ดูเหมือนงานเสร็จสมบูรณ์

## ข้อเท็จจริงที่ตรวจแล้ว
- `src/DnDEz.Domain/Entities/Campaigns.cs` มี class `Character` อยู่แล้ว — มี typed column
  CurrentHp/MaxHp/CurrentTempHp/ArmorClass/InitiativeModifier/Level แต่ **ไม่มี STR/DEX/CON/
  INT/WIS/CHA และไม่มี speed เลย** (ตรวจทั้ง Domain/Rules แล้ว 2026-09-04)
- `src/DnDEz.Api/Endpoints/EncounterEndpoints.cs` มี `DefaultMovementFeet = 30` hardcode ไว้
  พร้อม comment ยอมรับว่าชั่วคราว — Part นี้ทำให้มีที่มาจริง แต่ยังไม่ต้องแก้จุดนั้น (Part 8 ค่อยแก้)
- ตาราง `rules_corpus_entries` มีอยู่แล้ว (id, category, key, title, content, license, source)
  **ไม่มี campaign_id = เป็นของระดับระบบ** ใช้เก็บ "ข้อความกฎ" ไม่ใช่ "ตัวเลขที่คำนวณได้"
  → คลังกลไกต้องเป็นตารางใหม่ ห้ามยัดลงตารางนี้ (เหตุผลเต็มใน design doc §2.1)
- `campaigns.rules_system` เป็นคอลัมน์ที่มีอยู่แล้ว (fixture ใช้ค่า `srd2024`) — เผื่อหลายเวอร์ชันไว้แล้ว

## สิ่งที่ต้องทำ
1. Migration + entity ของคลังกลไก เก็บเฉพาะที่ engine ต้องใช้ (design doc §2.3):
   ชื่อ · ประเภท (อาวุธ/เวท/การเคลื่อนที่) · ระยะ · ชนิดเป้า · ราคา (action/bonus/reaction/ช่องเวท)
   · รูปทอย (vs AC หรือให้เป้า save) · ป้ายประเภทสำหรับให้ engine หยิบกฎทั่วไปมาใช้
   รายละเอียดที่ engine ไม่ใช้ (คำบรรยาย/upcast/องค์ประกอบร่าย) ให้อยู่ jsonb แยก
   **การเคลื่อนที่ (เดิน/กระโดด/ปีน) ก็เป็น entry ในคลังนี้เหมือนกัน** และต้องมีฟิลด์บอกว่า
   "แตะพื้นหรือไม่" (design doc §3.2)
2. เพิ่ม STR/DEX/CON/INT/WIS/CHA เป็น typed column ใน `Character` + speed
   (ใช้เลนส์เดียวกับ Sprint B Part 2: ทุกตัวละครมีแน่นอน ไม่ผันแปรตามคลาส → typed)
   migration ต้องมี default ให้ตัวละครเดิมไม่พัง
3. Seed ข้อมูลชุดแรก: อาวุธพื้นฐาน + cantrip + เวทระดับ 1 + วิธีเคลื่อนที่

## ⚠️ เรื่อง seed data (สำคัญ)
ห้าม dispatch งาน seed ให้ `haiku-worker` ในรูปแบบ "เขียนตารางอาวุธ SRD ออกมา" — จะเข้ากับดัก
ข้อ 4a ใน CLAUDE.md ตรง ๆ (2026-08-30: haiku แต่งตัวเลขที่ไม่ได้วัดจริง) ค่าระยะ/ดาเมจที่ผิด
จะไปหลอกผู้เล่นเงียบ ๆ ผ่าน affordance panel ซึ่งไม่มีอะไรจับได้
→ ส่ง haiku ได้เฉพาะกรณีมีตัวบท SRD จริงให้แปลงตามรูปแบบที่กำหนดชัด ไม่งั้นทำเองหรือส่ง sonnet

## RLS
คลังนี้เป็นของระดับระบบ (ไม่ผูกแคมเปญ) เหมือน `rules_corpus_entries` → **ตัดสินให้ชัดว่าต้องมี
policy แบบไหน** และเขียนเหตุผลลง migration · อย่าคิดเองว่า "ไม่มี campaign_id ก็ไม่ต้องทำอะไร"
โดยไม่ตรวจว่า role `dndez_app` อ่าน/เขียนอะไรได้บ้าง

## ห้ามทำ
- ห้ามแตะ `DefaultMovementFeet` ใน EncounterEndpoints (Part 8)
- ห้ามทำ endpoint ใด ๆ (Part 7/9 ค่อยใช้คลังนี้)
- ห้าม seed เวทเกินระดับ 1

## DoD
- (ข) novice check: N/A เพราะไม่มี UI — เขียนเหตุผลกำกับแทน
- (ค) orphan check: ตารางนี้จะยังไม่มี endpoint จนถึง Part 7/9 — **เขียนกำกับว่า "ตั้งใจเป็น orphan
  ชั่วคราว รอ Part 7/9"** ไม่ใช่ปล่อยเงียบ
- เทสต์: คลังต้องมีเทสต์ยืนยันค่าที่ seed ตรงกับที่ตั้งใจ + RLS state ของตารางใหม่
```

---

## Part 2 — Story outline + active beat

| | |
|---|---|
| **tier** | ✅ backend (ไฟล์ใหม่ + แตกงานได้) · ❌ frontend (ทำเองรวดเดียว) |
| **ถ้าไม่ใช้ tier** | backend: sonnet · frontend: **opus** |
| **security review** | ✅ ต้องมี (DM-only + external input + DB) |

```
<tier> Sprint G Part 2 (backend) — API ของ story_beats + active-beat

อ่านก่อน: docs/spatial-play-design.md §11 (กฎ route) และ §12 (active beat เก็บที่ไหน)

## ข้อเท็จจริงที่ตรวจแล้ว
- ตาราง `story_beats` มีอยู่แล้วตั้งแต่ InitialSchema: id, campaign_id, order, title, summary,
  objective, entry_trigger, exit_conditions(text[]), monster_refs(uuid[]), npc_refs(uuid[]),
  secrets(jsonb), dm_notes, progress, review_status
- `BeatProgress` enum มีอยู่แล้วใน `src/DnDEz.Domain/Enums/Enums.cs` = { Upcoming, Active, Passed }
- **ยังไม่มี endpoint ไหนแตะ story_beats เลย** (grep ทั้ง src/DnDEz.Api/Endpoints/ ได้ศูนย์)
- มี index `ix_story_beats_campaign_id_order` (campaign_id, order) อยู่แล้ว
- ตัวอย่าง pattern ที่ให้ลอก: `src/DnDEz.Api/Endpoints/SessionEndpoints.cs` (CRUD ธรรมดา)
  และ `src/DnDEz.Api/Auth/CampaignAuthorization.cs` → ใช้ `RequireCampaignDmAsync` สำหรับ DM-only
  (ห้ามเขียนเช็คสิทธิ์เองซ้ำ — งานที่ 3 ของ Sprint F ทำ helper นี้ไว้แล้ว)

## สิ่งที่ต้องทำ
1. Endpoint (ทุกอันต้องขึ้นต้น `/campaigns/{campaignId:guid}` — ดู §11 ว่าทำไม):
   GET/POST `/campaigns/{cid}/beats` · PATCH/DELETE `/campaigns/{cid}/beats/{bid}`
   PATCH `/campaigns/{cid}/beats/order` (รับ array ของ beat_id เรียงใหม่ทีเดียว)
   POST `/campaigns/{cid}/sessions/{sid}/active-beat`
2. **active-beat เป็น endpoint เดี่ยว ไม่ใช่ PATCH ธรรมดา** (เหตุผลเต็ม §12.1):
   - เขียนสองแถวในธุรกรรมเดียว (beat เดิม → Passed, beat ใหม่ → Active)
   - broadcast event ออก SignalR group ของแคมเปญ
3. Migration เพิ่ม partial unique index บังคับ active ได้ทีละอัน:
   `CREATE UNIQUE INDEX ix_story_beats_one_active_per_campaign ON story_beats (campaign_id)
    WHERE progress = 'Active';`
   (pattern เดียวกับ `ix_scenes_one_active_per_session` ที่มีอยู่แล้วใน DB)
4. เทสต์ครอบ: DM-only (non-DM ได้ 403), cross-campaign isolation, active ซ้อนกันสองอันไม่ได้,
   reorder แล้วลำดับถูก, broadcast ยิงจริง

## ห้ามทำ
- ห้ามทำ frontend ใน dispatch นี้ (แยกทำเองต่างหาก)
- ห้ามแตะ monster_refs/npc_refs (Part 3)
- ห้ามเพิ่ม `sessions.active_beat_id` — ตัดสินแล้วว่าเก็บที่ `story_beats.progress` (§12)

## DoD
(ค) orphan check: หลัง Part นี้ `story_beats` ต้องมี endpoint ครบแล้ว บันทึกผล grep ไว้
```

```
Sprint G Part 2 (frontend) — Story outline ในหน้า DM console

⚠️ ห้ามใช้ <tier> — CLAUDE.md ระบุว่างาน UI ที่ layout–state–ดีไซน์ต้องสอดคล้องกันทั้งหน้า
ให้ทำเองรวดเดียว (บทเรียนจาก defect A4) · แนะนำใช้ claude-opus-5

## ข้อเท็จจริงที่ตรวจแล้ว
- `frontend/src/components/DmConsole.tsx` คือหน้า DM ปัจจุบัน — ใช้ `WorkspaceGrid`/
  `WorkspaceColumn` จาก `campaign-panels.tsx` เป็นโครง 3 คอลัมน์
- pattern ที่ให้ลอก: panel อื่นใน `campaign-panels.tsx` + `Badge`/`PanelLabel` ใน
  `combat-panels.tsx` (มี badge tone: neutral/active/good/warn/danger อยู่แล้ว)
- query/mutation ใช้ TanStack Query ตาม `use-campaign-workspace.ts`

## สิ่งที่ต้องทำ
คอลัมน์ซ้ายของ DM console: รายการ beat + ปุ่มเพิ่ม + ลากจัดลำดับ + กดตั้ง active
สถานะ 3 แบบตาม BeatProgress: Passed = ✓ จาง · Active = ไฮไลต์ + ▶ · Upcoming = ปกติ
mockup อ้างอิง: `docs/mockups/uxpilot-dm-console-scene-prep.png` — คอลัมน์ซ้าย
"STORY OUTLINE / PLOT BEATS"

## ห้ามทำ
ห้ามทำตาม mockup ในส่วนที่อยู่นอกขอบเขต Sprint G — `Voice to DM`, `AI DM โทนเรื่อง`,
การ์ด spawn NPC (ดูหัวข้อบนสุดของไฟล์นี้)

## DoD
(ข) novice check: DM กดเปลี่ยน beat แล้วเห็นเครื่องหมายเปลี่ยนทันทีโดยไม่ต้องเดา — เขียนคำตอบลงสรุป
ทดสอบจริงในเบราว์เซอร์ ไม่ใช่แค่ tsc/lint ผ่าน
```

---

## Part 3 — Scene prep panel

| | |
|---|---|
| **tier** | ✅ backend · ❌ frontend |
| **ถ้าไม่ใช้ tier** | backend: sonnet · frontend: **opus** |
| **security review** | ✅ ต้องมี — panel นี้มี **ความลับที่ผู้เล่นห้ามเห็น** |

```
<tier> Sprint G Part 3 (backend) — API อ่าน monster_stat_blocks + npc_profiles ตาม active beat

อ่านก่อน: docs/spatial-play-design.md §13.2 (Scene prep เป็นแผงเปิดค้าง) และ §4.1
(ทำไม DM review สำคัญขึ้นเมื่อมี affordance)

## ข้อเท็จจริงที่ตรวจแล้ว
- `monster_stat_blocks`: id, campaign_id, source_document_id, name, armor_class, hit_points,
  actions(jsonb), condition_refs(jsonb), review_status
- `npc_profiles`: id, campaign_id, source_document_id, name, motive, voice_hint,
  knows_secrets(jsonb), review_status
- `story_beats.monster_refs` / `npc_refs` เป็น `uuid[]` ชี้ไปหาสองตารางนี้
- **ยังไม่มี endpoint ไหนแตะทั้งสองตารางเลย**
- กฎที่ล็อกไว้ใน PROJECT-CONTEXT §2: record ที่ `review_status != approved`
  **ห้ามส่งเข้า orchestrator context**

## สิ่งที่ต้องทำ
- GET endpoint (DM-only) คืน statblock + NPC ของ beat ที่ระบุ — รับ beatId เป็น parameter
  เพื่อให้ frontend "peek" beat อื่นได้โดยไม่เปลี่ยน active beat
- ใช้ `CampaignAuthorization.RequireCampaignDmAsync` — ห้ามเขียนเช็คสิทธิ์เอง
- เทสต์: non-DM ได้ 403 · cross-campaign ได้ 404 · `secrets`/`knows_secrets` ต้องไม่หลุดออก
  ไปทาง endpoint ที่ผู้เล่นเรียกได้ (ยิงด้วย token ผู้เล่นแล้วยืนยันว่าไม่มี field นั้นใน JSON
  ไม่ใช่แค่ UI ไม่แสดง — บทเรียน Sprint F งานที่ 7)

## ห้ามทำ
- ห้ามทำ pipeline สแกนหนังสือ (นอกขอบเขต — ตารางมีข้อมูลอยู่แล้วหรือ seed มือได้)
- ห้ามทำ endpoint แก้/สร้าง statblock (อ่านอย่างเดียวใน Part นี้)
```

```
Sprint G Part 3 (frontend) — แผง Scene prep

⚠️ ห้ามใช้ <tier> · แนะนำ claude-opus-5

## สิ่งที่ต้องทำ
แท็บในคอลัมน์ขวาของ DM console (คู่กับ AI config) — statblock ศัตรู, condition ที่ใช้ได้,
NPC ในฉาก, กล่องความลับ DM
- **ผูกกับ active beat อัตโนมัติ** เปลี่ยน beat แล้วเนื้อหาเปลี่ยนเอง
- **peek beat ถัดไปได้โดยไม่เปลี่ยน active beat จริง**

## เทสต์ที่ต้องมี (สำคัญ)
เขียนเทสต์ที่ **ล้มก่อนแก้จริง** ยืนยันว่า peek ไม่ยิง `active-beat` endpoint
(บทเรียน: เทสต์ที่ผ่านตั้งแต่ยังไม่แก้บั๊ก = เทสต์ที่ไม่ได้ทดสอบอะไรเลย — เจอมาแล้ว 2 ครั้ง
ตอนทำ campaign-hub-manager)

## DoD
(ข) novice check: DM เปลี่ยน beat แล้วเห็น statblock เปลี่ยนตามทันทีโดยไม่ต้องกดหา
(ค) orphan check: `monster_stat_blocks` + `npc_profiles` ต้องมี endpoint แล้ว
```

---

## Part 4 — Scene foundation (แผนที่ + grid)

| | |
|---|---|
| **tier** | ✅ ใช้ |
| **ถ้าไม่ใช้ tier** | opus |
| **security review** | ✅ **บังคับ เข้มที่สุดในทั้ง 10 Part** (file upload) |

```
<tier> Sprint G Part 4 — สร้าง scene + อัปโหลดแผนที่ + จัดกริด

อ่านก่อน: docs/spatial-play-design.md §11.1 (รายการ endpoint) และ §22 (ทำไมตัด Generate with AI)

## ข้อเท็จจริงที่ตรวจแล้ว
- ตาราง `scenes`: id, session_id, name, grid(jsonb), tokens(jsonb), fog_layer(jsonb),
  background_image_url, is_active — มี index `ix_scenes_one_active_per_session`
  UNIQUE (session_id) WHERE is_active = true อยู่แล้ว
- ตาราง `map_assets`: id, campaign_id, source_document_id, storage_key, crop_source(jsonb),
  grid_alignment(jsonb), review_status
- **ยังไม่มี endpoint ไหนแตะทั้งสองตาราง**
- policy ของ `scenes` อ้อมผ่าน session:
  `EXISTS (SELECT 1 FROM sessions s WHERE s.id = scenes.session_id AND s.campaign_id = app_current_campaign())`
  → **route ต้องมี campaignId ไม่งั้นมองไม่เห็นแถวไหนเลย**
- MinIO มีอยู่ใน docker-compose แล้ว (port 9000/9001, user/pass dndez/dndez12345)
- `storage_quotas` เป็นตารางที่มีอยู่แล้ว — ตรวจว่าต้องผูกด้วยไหม

## สิ่งที่ต้องทำ
POST/GET `/campaigns/{cid}/sessions/{sid}/scene` · PATCH `/campaigns/{cid}/scenes/{scid}/grid`
+ อัปโหลดไฟล์แผนที่เข้า map_assets/MinIO + เครื่องมือจัดกริด (ล็อก pixel-per-square)

## ⚠️ security — Part นี้เสี่ยงสุด ต้อง dispatch security-reviewer เสมอ
ตรวจอย่างน้อย: ชนิดไฟล์จริงจาก magic bytes (ไม่ใช่เชื่อนามสกุลหรือ Content-Type),
ขนาดสูงสุด, ชื่อไฟล์/path traversal, signed URL ต้องมีอายุสั้น, quota ต่อบัญชี,
และผู้ที่ไม่ใช่สมาชิกแคมเปญต้องเข้าถึงไฟล์ไม่ได้แม้รู้ URL

## ห้ามทำ
- **ห้ามทำปุ่ม "Generate with AI"** — ขัดกับการตัดสินใจที่ล็อกไว้ 2026-08-21 (Claude API สร้างรูป
  ไม่ได้ ต้องพึ่ง provider อื่น = BYOK ซับซ้อนขึ้น) ดู design doc §22
- ห้ามทำ token (Part 5) · ห้ามทำ fog (Part 6)

## DoD
(ข) novice check: DM อัปโหลดแผนที่แล้วเห็นกริดทาบถูกสัดส่วนโดยไม่ต้องคำนวณเอง
```

---

## Part 5 — Tokens

| | |
|---|---|
| **tier** | ✅ ใช้ |
| **ถ้าไม่ใช้ tier** | opus |
| **security review** | ✅ ต้องมี (ใครขยับ token ของใครได้) |

```
<tier> Sprint G Part 5 — token บนแผนที่ (ephemeral + committed)

อ่านก่อน: docs/spatial-play-design.md §14 ทั้งหัวข้อ (สำคัญมาก — อธิบายว่าทำไมเขียนผ่าน hub ไม่ได้)

## ข้อเท็จจริงที่ตรวจแล้ว
- `scenes.tokens` เป็น jsonb ที่มีอยู่แล้ว (Part 4 สร้าง scene ได้แล้ว)
- `IEncounterTurnStateStore` ใน `src/DnDEz.Api/Encounters/` เป็น pattern Redis ที่มีอยู่แล้ว
  พร้อม compare-and-set + version (จาก Sprint F งานที่ 5)
- เทสต์ตัวอย่างที่ให้ลอก: `Active_encounter_falls_back_to_full_turn_resources_when_redis_is_unavailable`
  ใน `tests/DnDEz.Api.Tests/EncounterEndpointsTests.cs`
- **SignalR hub เขียน state ไม่ได้** — middleware อ่าน campaignId จาก route value,
  hub invocation ไม่มี route → `app.campaign_id` เป็น NULL → มองไม่เห็นแถวไหนเลย

## สิ่งที่ต้องทำ
1. POST/PATCH/DELETE `/campaigns/{cid}/scenes/{scid}/tokens[/{tid}]`
2. token ต้อง**ผูกกับ entity จริง** (`characters` หรือ `monster_stat_blocks`) ไม่ใช่แค่รูปวงกลม
   — เหตุผล: Part 8/9 ต้องคำนวณจากตำแหน่ง+ระยะเอื้อมของ entity นั้น ถ้า token เป็นแค่รูป
   จะคำนวณอะไรไม่ได้เลย
3. ลาก = ghost broadcast ผ่าน hub (ไม่ persist ไม่มีอำนาจ) · วาง = PATCH ครั้งเดียว
4. **Postgres เป็น source of truth** — Redis เป็นแค่ตัวลดจำนวนการเขียน
   ต่างจาก turn state ที่ล็อกไว้ว่า "Redis หาย = รีเซ็ตเทิร์น" เพราะ turn state สร้างใหม่ได้
   แต่ตำแหน่ง token สร้างใหม่ไม่ได้ → Redis ใช้ไม่ได้ให้ถอยไปอ่าน-เขียน Postgres ตรง ๆ
   พร้อม log warning ห้ามยอมให้ข้อมูลหาย
5. persist ทันทีที่: เปลี่ยนเทิร์น / เปลี่ยน fog / จบ encounter / ปิด session + flush ตามเวลา

## security
ใครขยับ token ไหนได้บ้าง — DM ขยับได้ทุกตัว, ผู้เล่นขยับได้เฉพาะตัวละครที่ตัวเองเป็นเจ้าของ
ใช้ `CampaignAuthorization.RequireCharacterActorAsync` ที่มีอยู่แล้ว

## DoD
(ข) novice check: ลาก token แล้วตำแหน่งรอดหลัง refresh หน้า
เทสต์: Redis ใช้ไม่ได้แล้วยัง PATCH สำเร็จผ่าน Postgres (ลอก pattern เทสต์ที่อ้างถึงข้างบน)
```

---

## Part 6 — Fog + สามมุมมอง + SignalR ต่อ role

| | |
|---|---|
| **tier** | ✅ backend · ❌ frontend (3 หน้าจอ = 3 design pass) |
| **ถ้าไม่ใช้ tier** | **opus ทั้งคู่** |
| **security review** | ✅ **บังคับ** — Part ที่รั่วง่ายที่สุด |

```
<tier> Sprint G Part 6 (backend) — fog + projection ต่อ role + SignalR group ต่อ role

อ่านก่อน: docs/spatial-play-design.md §13 ทั้งหัวข้อ (โดยเฉพาะ §13.1 ตาราง projection
และ §13.3 การกรองต้องเกิดที่ server เท่านั้น)

## ข้อเท็จจริงที่ตรวจแล้ว
- `scenes.fog_layer` เป็น jsonb ที่มีอยู่แล้ว
- ปัจจุบัน broadcast ทุกตัวยิงเป็น `Clients.Group(CampaignHub.GroupName(campaignId))`
  = ทุกคนได้ payload ก้อนเดียวกัน
- **มีที่แหกกฎนี้ไปแล้วหนึ่งที่**: `src/DnDEz.Api/Endpoints/CombatEndpoints.cs` ส่ง `HpUpdated`
  ด้วย `Clients.User(...)` เฉพาะ DM เมื่อเป้าเป็น NPC ที่ซ่อน stat (Sprint F งานที่ 7 fix f10f87a)
  → ใช้เป็น pattern อ้างอิงได้ แต่ Part นี้ต้องทำให้เป็นระบบ ไม่ใช่ special case
- `CharacterStatsVisibility.IsVisibleTo(...)` ใน `CharacterEndpoints.cs` เป็น helper ที่มีอยู่แล้ว

## สิ่งที่ต้องทำ
1. POST `/campaigns/{cid}/scenes/{scid}/fog/reveal` และ `/fog/hide`
2. GET `.../scene` **ตัวเดียวกัน** คืน payload คนละหน้าตาตาม role (ตาราง §13.1):
   - HP ปาร์ตี้ = **ตัวเลขจริงทั้ง 3 มุมมอง** (ตัดสินแล้ว §13.1 — ยึด player-side-design §12
     ที่ให้เหตุผลว่า healer ต้องคำนวณได้ · มือใหม่ประเมินเองไม่ได้)
   - HP ศัตรู = DM เท่านั้น
   - token/ตำแหน่งที่ยังอยู่ใน fog = **ต้องไม่อยู่ใน JSON เลย** ไม่ใช่ส่งไปแล้วให้ client ซ่อน
3. SignalR แยก group ต่อ role

## ⚠️ security — บังคับ dispatch security-reviewer
ทดสอบแบบ adversarial: ยิง GET `.../scene` ตรง ๆ ด้วย token ของผู้เล่น (ไม่ผ่าน UI)
แล้วยืนยันว่า **ไม่มี field ของ token ที่อยู่ใน fog ปนมาในเนื้อ JSON เลย**
และ **ห้าม apply fix ที่ reviewer เสนอโดยไม่พิสูจน์เอง** (CLAUDE.md — เจอ false fix 3 ครั้ง
ในงานเดียวมาแล้ว) ต้องเขียนเทสต์ที่ red ก่อนแก้จริงเสมอ

## ห้ามทำ
ห้ามทำ affordance panel (Part 9) · ห้ามทำ credential ของจอกลางในรอบนี้ถ้าเวลาไม่พอ
ให้แยกเป็น subtask ท้ายสุดและระบุชัดถ้าไม่ได้ทำ
```

```
Sprint G Part 6 (frontend) — สามมุมมองที่เป็นคนละหน้าจอจริง

⚠️ ห้ามใช้ <tier> · claude-opus-5

## สิ่งที่ต้องทำ
DM view / Player view / Table view — **ต้องเป็นคนละ component จริงในโค้ด**
ไม่ใช่หน้าเดียวกันที่ซ่อนด้วย conditional render
(บทเรียนจาก mockup รอบแรก: ปุ่ม 3 อันชี้ไป component เดียวกัน ตรวจพบตอนรีวิว)
- Table view: ไม่มี input ใด ๆ, ไม่มีความลับ DM, ตัวอักษรใหญ่อ่านจาก 1-2 เมตรได้
- mockup อ้างอิง: `docs/mockups/uxpilot-table-view.png` (สังเกต PARTY HEALTH รวม + ไม่มี input เลย)
  และ `docs/mockups/uxpilot-player-view.png` (สังเกต fog บังพื้นที่ที่ยังไม่เปิด)

## DoD
(ข) novice check: ผู้เล่นเข้าใจว่ามีพื้นที่ที่ยังไม่รู้จัก โดยไม่ต้องรู้คำว่า "fog of war"
```

---

## Part 7 — Character preset

| | |
|---|---|
| **tier** | ✅ ใช้ |
| **ถ้าไม่ใช้ tier** | sonnet |
| **security review** | ⚠️ เบา (authorization การสร้างตัวละคร) |

```
<tier> Sprint G Part 7 — คลัง preset ตัวละคร + seed ชุดแรก

อ่านก่อน: docs/spatial-play-design.md §5 ทั้งหัวข้อ + player-side-design.md §18–19

## ข้อเท็จจริงที่ตรวจแล้ว
- ตาราง `character_presets` มีอยู่แล้วและตรงกับ player-side-design §18–19 แบบ 1:1:
  scope, campaign_id, source, name, build(jsonb), level_path(jsonb), level,
  build_fingerprint, popularity_count, srd_only, created_at
- `Character.HarvestConsent` มีอยู่แล้ว (= §19 กฎข้อ 1)
- **ยังไม่มี endpoint ไหนแตะ character_presets**
- `POST /campaigns/{cid}/characters` มีอยู่แล้วใน `CharacterEndpoints.cs` — ปัจจุบันรับแค่ชื่อ
  แล้วได้ HP 10/10, AC 10, level 1 ตายตัว (placeholder)
- `srd_only` คือจุดบังคับ §19 กฎข้อ 3 (ของจาก source scan ห้ามเข้าคลังกลาง)

## สิ่งที่ต้องทำ
1. GET list preset (scope global + campaign ของตัวเอง) + POST สร้างตัวละครจาก preset
2. Seed preset **authored** 6–12 ตัว ครอบคลาสพื้นฐาน level 1 พร้อม `level_path`
   ใช้อาวุธ/เวทจากคลังของ Part 1 (อ้างอิงด้วย id ไม่ใช่ hardcode ตัวเลขซ้ำ)
3. Frontend: ตอนสร้างตัวละครให้เลือกจากการ์ด preset แทนกรอกชื่อเปล่า
   mockup อ้างอิง: `docs/mockups/uxpilot-character-prep.png` ("CHOOSE YOUR HERO")

## ⚠️ เรื่อง seed (เหมือน Part 1)
ห้ามให้ haiku "เขียน build ของนักรบ level 1 ออกมาเอง" — ต้องมีที่มาอ้างอิงได้
ถ้าตัวเลขผิดจะไปโผล่เป็นตัวละครจริงที่ผู้เล่นใช้เล่น

## ห้ามทำ
- ห้ามทำ harvest (§19 — เก็บ preset จากตัวละครที่เล่นจบ) ในรอบนี้ · ทำแค่ authored
- ห้ามทำ builder แบบเต็ม (สร้างเองตั้งแต่ต้น) — ปุ่ม "สร้างใหม่เอง" ทำเป็น placeholder ได้

## DoD
(ข) novice check: เลือก "นักรบ level 1" แล้วได้ดาบยาว+โล่+HP จริงทันที
ไม่ต้องรู้ว่าดาบยาวทอย 1d8 — เขียนคำตอบลงสรุป
(ค) orphan check: `character_presets` ต้องมี endpoint แล้ว
```

---

## Part 8 — Geometry: ต้นทุนการเคลื่อนที่

| | |
|---|---|
| **tier** | ⚠️ ใช้เฉพาะเพื่อแยก **คนเขียนเทสต์ ≠ คนเขียนโค้ด** — **ห้ามหั่นตัว algorithm ให้หลายคนทำ** |
| **ถ้าไม่ใช้ tier** | **opus** |
| **security review** | ❌ ไม่ต้อง (`DnDEz.Rules` ไม่รู้จัก user/สิทธิ์เลยตามที่ล็อกไว้) |

```
<tier> Sprint G Part 8 — คำนวณต้นทุนการเคลื่อนที่บนกริด (DnDEz.Rules)

อ่านก่อน: docs/spatial-play-design.md §3 ทั้งหัวข้อ (§3.1 pathfinding, §3.2 วิธีเคลื่อนที่
กำหนดว่าพื้นมีผลไหม, §3.3 พื้นสองแหล่ง)

## ⚠️ วิธี dispatch สำหรับ Part นี้โดยเฉพาะ
algorithm นี้เป็นก้อนเดียว (pathfinding + ต้นทุนพื้น + โหมดเคลื่อนที่ เกี่ยวพันกันหมด)
→ **ห้ามหั่นให้ worker หลายตัวทำคนละส่วนของ algorithm**
ใช้ tier เพื่อ: owner เขียนเทสต์ (red) จาก spec → dispatch การเขียน implementation (green)
ออกไปตัวเดียว ตามข้อ 4 ของ CLAUDE.md (กัน blind spot คนเขียนเทสต์ทับคนเขียนโค้ด)

## ข้อเท็จจริงที่ตรวจแล้ว
- `src/DnDEz.Rules/` เป็น zero-dependency (BCL ล้วน ไม่มี PackageReference เลย)
  **ห้าม reference EF Core / Infrastructure / orchestrator** · ไม่เขียน DB เอง คืน record ล้วน
  · ฉีด `IDiceRoller` เข้าไปให้ deterministic
- ที่มีอยู่แล้ว: ActionEconomyResolver, AdvantageResolver, AttackResolver, CheckResolver,
  DamageResolver, DeathSaveResolver, DiceEngine, DiceExpressionParser, HitPointResolver
- `AdvantageResolver.Resolve(bool hasAdvantage, bool hasDisadvantage)` มีอยู่แล้ว —
  รวมหลายแหล่งตามกฎ 2024 (มีทั้งคู่ = หักล้าง) **ใช้ตัวนี้ ห้ามเขียนใหม่**
- `EncounterEndpoints.DefaultMovementFeet = 30` เป็น hardcode ที่ Part นี้ต้องเปลี่ยนมาอ่าน
  speed จริงของตัวละคร (Part 1 เพิ่มฟิลด์ speed ไว้แล้ว)

## สิ่งที่ต้องทำ
1. ฟังก์ชันคำนวณ: (เส้นทาง, วิธีเคลื่อนที่, พื้นแต่ละช่อง, effect ที่ติดตัว) → ต้นทุน
   **ไม่ใช่ `ระยะทาง × ตัวคูณพื้น`**
2. "เดินได้ถึงไหน" = กระจายตามต้นทุน (pathfinding) **ไม่ใช่วงกลมรัศมี**
   — ถ้ามีพื้นเดินยากขวาง ช่องที่ไกลเท่ากันจะไปถึงได้ไม่เท่ากัน และอ้อมอาจถูกกว่าลุยตรง
3. วิธีเคลื่อนที่กำหนดว่าคิดผลพื้นไหม (เดิน=คิด, กระโดด/ปีน/บิน=ไม่คิด)
   catalog entry ของแต่ละวิธีบอกแค่ "แตะพื้นหรือไม่" ส่วนกฎอยู่ใน engine
4. พื้นสองแหล่งรวมกัน: จากแผนที่ (`scenes.grid`) + จาก effect (Web/Grease — ผูกกับ encounter
   ไม่ใช่แผนที่ เพราะหมดเวลาแล้วต้องลบออกได้)

## ⚠️ ที่ยังไม่ได้ตรวจ — ต้องเช็คก่อนเขียน
รายละเอียดกฎจริงของ SRD 5.2 เรื่องกระโดดข้ามพื้นเดินยาก (คิดยังไงตอนลงพื้น) **ยังไม่ได้ตรวจ**
ที่ตกลงกันไว้คือ *โครงสร้าง* ไม่ใช่ตัวเลข — ต้องเปิด SRD ยืนยันก่อน ห้ามเดา

## ห้ามทำ
ห้ามทำ affordance panel (Part 9) · ห้ามทำ OA (Part 10) · ห้ามให้ Rules รู้จัก user/สิทธิ์

## DoD
(ข) novice check: วาง difficult terrain ในฉากทดสอบ แล้วยืนยันว่าระยะที่เหลือลดเร็วกว่าปกติ
ตอนเดินผ่าน แต่ไม่ลดถ้าเลือก "กระโดดข้าม" — นี่คือตัวอย่างที่เจ้าของโปรเจกต์ยกไว้ตอนออกแบบ
```

---

## Part 9 — Affordance panel

| | |
|---|---|
| **tier** | ✅ backend · ❌ frontend |
| **ถ้าไม่ใช้ tier** | **opus ทั้งคู่** |
| **security review** | ✅ **บังคับ** — รายการ action เป็นช่องรั่วในตัวมันเอง |

```
<tier> Sprint G Part 9 (backend) — คำนวณ "ทำอะไรได้บ้าง" ต่อผู้ดูแต่ละคน

อ่านก่อน: docs/spatial-play-design.md §1.2 (server คำนวณ + รายการ action เป็นช่องรั่ว),
§1.3 (เป็น rules engine ไม่ใช่ AI), §1.4 (โมเดล 3 ระดับ), §2.2 (catalog=data engine=rules)

## ข้อเท็จจริงที่ตรวจแล้ว
- `AttackResolver.Resolve(RollResult roll, int targetArmorClass)` — รับผลทอยกับ AC
  แล้วบอกเข้า/พลาด/คริต **ไม่รู้จักอาวุธหรือระยะเลย** → Part นี้เป็นชั้นบนที่ตอบว่า
  "อะไรเป็นไปได้" แล้วส่งต่อให้ชั้นเดิมตอบว่า "ผลเป็นยังไง"
- `AdvantageResolver.Resolve(hasAdvantage, hasDisadvantage)` มีอยู่แล้ว — ที่ขาดคือ
  **คนตัดสินว่า hasDisadvantage = true** เพราะร่ายระยะไกลตอนศัตรูประชิด
- `ActionEconomyResolver` มีอยู่แล้ว (action/bonus/reaction/movement)
- คลังกลไกจาก Part 1 + ตำแหน่งจาก Part 5 + ต้นทุนเคลื่อนที่จาก Part 8

## สิ่งที่ต้องทำ
คำนวณรายการตัวเลือกต่อผู้ดูแต่ละคน ตามโมเดล 3 ระดับ:
1. ทำไม่ได้ → **ไม่อยู่ในรายการเลย** (ไม่ใช่ส่งไปแล้วให้ client ซ่อน)
2. ทำได้แต่มีราคา → อยู่ในรายการ **พร้อมเหตุผลเป็นข้อความภาษาคน**
   ("ร่ายเวทระยะไกลตอนศัตรูประชิด → ติด disadvantage")
3. ทำได้ปกติ → อยู่ในรายการเฉย ๆ

**กฎทั่วไปอยู่ใน engine ไม่ใช่ใน catalog** — "การโจมตีระยะไกล + มีศัตรูประชิด = disadvantage"
เขียนครั้งเดียวครอบทุกเวท/อาวุธ/มอนสเตอร์ ห้ามเขียนเงื่อนไขซ้ำในทุกรายการของ catalog

## ⚠️ security — บังคับ dispatch security-reviewer
**รายการ "ทำอะไรได้" เป็นช่องรั่วของข้อมูลในตัวมันเอง** — ถ้าบอกว่า "โจมตีก็อบลินได้"
= บอกว่ามีก็อบลินอยู่ ต้องคำนวณจาก "สิ่งที่คนนั้นมองเห็น" เท่านั้น
ไม่ใช่คำนวณจากความจริงทั้งหมดแล้วค่อยกรองตอนส่ง
ทดสอบ: ผู้เล่นที่ยังไม่เปิด fog ตรงนั้น ต้องไม่ได้ตัวเลือกที่พาดพิงถึงศัตรูที่ยังมองไม่เห็น

## ห้ามทำ
ห้ามใช้ AI/LLM ในการคำนวณส่วนนี้เด็ดขาด — เป็น deterministic ล้วน (§1.3 อธิบายว่าทำไม)
```

```
Sprint G Part 9 (frontend) — พาเนลตัวเลือกของผู้เล่น

⚠️ ห้ามใช้ <tier> · claude-opus-5

## สิ่งที่ต้องทำ
แทนที่ช่องกรอก `1d20+5`/`1d8+3` ใน `AttackCheckPanel` ด้วยรายการที่คำนวณมาแล้ว
mockup อ้างอิง: `docs/mockups/uxpilot-player-view.png` (พาเนล "คุณจะทำอะไร?")
— มี 4 สถานะให้ลอก: ปกติ / DISADVANTAGE (ป้ายแดง + กล่องเหตุผล) / ADVANTAGE (ป้ายเขียว)
/ ใช้ไปแล้ว (เทาจาง)

## DoD
(ข) novice check: ผู้เล่นใหม่ที่ไม่รู้กฎเลย เจอศัตรูอยู่ไกล 70 ฟุตถือดาบประชิด
→ **ไม่เห็น**ตัวเลือกโจมตีประชิดตัวนั้นเลย (ไม่ใช่เห็นแล้วโดน error ทีหลัง)
```

---

## Part 10 — Reaction / Opportunity attack

| | |
|---|---|
| **tier** | ✅ ใช้ — และเป็น Part เดียวที่น่าจะต้อง escalate ถึง `fable-worker` (ข้ามบริบทเยอะสุด) |
| **ถ้าไม่ใช้ tier** | **opus** (ตึงมือ — ถ้าติดให้ซอยเป็น subtask ก่อน) |
| **security review** | ✅ ต้องมี (ใครมีสิทธิ์ตอบ + race condition) |

```
<tier> Sprint G Part 10 — reaction / opportunity attack พร้อม timeout

อ่านก่อน: docs/spatial-play-design.md §3.4 (OA และการเลือกเส้นทาง)
+ player-side-design.md §4 หัวข้อ "Reaction prompt"

## ข้อเท็จจริงที่ตรวจแล้ว
- `campaigns.reaction_timeout_seconds` **มีอยู่แล้วตั้งแต่ InitialSchema** (default 30,
  check constraint 5–300) แต่ **ไม่มีโค้ดที่ไหนอ่านมันเลย** — มีแค่ส่งค่าออก API
  → Part นี้คือที่ที่มันจะถูกใช้จริงครั้งแรก
- `ActionEconomyResolver` มี Reaction เป็น boolean resource อยู่แล้ว (ใช้ไปหรือยัง)
  แต่**ไม่มีตัว trigger** — ไม่มีอะไรตัดสินว่า "ตอนนี้มีสิทธิ์ใช้ reaction แล้วนะ"
- player-side-design §4 ล็อกไว้แล้วว่า: หมดเวลา = ถือว่าไม่ใช้ reaction แล้วเกมเดินต่อ
  (กัน pacing สะดุด โดยเฉพาะโหมด in-person)

## สิ่งที่ต้องทำ
1. ตรวจจับ trigger จากการเคลื่อนที่ (ใช้ geometry จาก Part 8) — เดินออกจากระยะเอื้อมของศัตรู
2. หยุดกลางการเคลื่อนที่ → ถามคนที่มีสิทธิ์ → รอ ≤ timeout → resolve → เดินต่อ
3. แจ้งผ่าน SignalR ไปหาเจ้าของสิทธิ์เท่านั้น (ไม่ broadcast ทั้งกลุ่ม)
4. หมดเวลาแล้วต้องเดินต่อได้เองโดยไม่ค้าง

## ⚠️ จุดที่ยากที่สุด
เป็นกลไก interrupt ที่ไม่เคยมีในระบบเลย — turn state ปัจจุบันเดินหน้าอย่างเดียว
(DM กด "เทิร์นถัดไป") ต้องออกแบบว่า state ระหว่างรอ reaction เก็บที่ไหน และถ้า client
หลุดกลางทางหรือ Redis หายจะกู้ยังไง — **ถ้าประเมินว่าเกินมือให้ escalate ขึ้น fable-worker
ตามข้อ 5 ห้ามฝืนทำเองแล้วได้ของครึ่ง ๆ กลาง ๆ**

## DoD
(ข) novice check: ผู้เล่นเดินออกจากระยะก็อบลิน → คนคุมก็อบลินเห็น prompt พร้อมนับถอยหลัง
โดยไม่ต้องมีใครจำกฎ OA ได้เอง · และคนเดินต้องเห็นคำเตือน**ก่อนกด** ว่าเส้นทางนี้จะโดนสวน
(ไม่ใช่กับดักที่เหยียบแล้วค่อยรู้ — §3.4)
```

---

## ของค้างที่ยังไม่มีเจ้าของ (ต้องกลับมา)

บันทึกไว้ตรงนี้เพราะเป็นรูปแบบเดียวกับที่ทำให้ Sprint F พลาด (เลื่อนแล้วไม่มีคนหยิบกลับ):

| ของค้าง | ควรกลับมาเมื่อไหร่ |
|---|---|
| โหมดสว่าง (mockup ยังไม่ได้ทำ) | ก่อนปิด Sprint G — ไม่ใช่ก่อนเริ่ม Part 1 |
| label `"แสดง HP ศัตรูให้ DM เห็น"` ใน mockup Scene Prep (ความหมายกลับด้าน — DM ต้องเห็น HP ศัตรูเสมอ ไม่ควรมี toggle ปิดสำหรับตัว DM เอง · toggle ที่ควรมีคือ "ผู้เล่นเห็นไหม") | ตอนทำ Part 3 frontend |
| ภาพ mockup ของ Part 10 (reaction prompt) | ก่อนเริ่ม Part 10 |

## ภาพ mockup ที่เก็บไว้แล้ว (`docs/mockups/`)

| ไฟล์ | ใช้กับ Part |
|---|---|
| `uxpilot-dm-console-scene-prep.png` | 2 (Story outline), 3 (Scene prep), 4 (แผนที่) |
| `uxpilot-dm-console-ai-config.png` | — (autonomy dial + proposal queue ทำไว้แล้วใน Sprint F ใช้เป็นอ้างอิงเฉย ๆ) |
| `uxpilot-player-view.png` | 6 (fog), 9 (affordance panel 4 สถานะ) |
| `uxpilot-table-view.png` | 6 (Table view ที่ไม่มี input) |
| `uxpilot-character-prep.png` | 7 (preset picker) |
| `aidesigner-player-action-movement-cost.png` | **8 (ต้นทุนการเคลื่อนที่)** — ภาพเดียวที่มี `30FT (เดินปกติ)` / `60FT (วิ่ง/พื้นเดินยาก)` · ชุด UXPilot ไม่มี **ห้ามลบ** |

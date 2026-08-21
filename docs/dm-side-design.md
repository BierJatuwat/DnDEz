# AI DM Platform — DM-side design

เอกสารรวมการออกแบบฝั่ง DM ทั้งหมด (ฝั่ง Player ยังไม่ได้ออกแบบ — เป็นขั้นถัดไป)

---

## 1. วิสัยทัศน์โปรเจกต์

แพลตฟอร์มเล่น D&D ที่ปรับตัวตามสถานการณ์จริงของคนเล่น — ไม่ว่าจะมีอุปกรณ์เครื่องเดียวหรือครบทุกคน
อยู่ห้องเดียวกันหรือคนละที่ โดยมี **AI เป็นผู้ช่วยของ DM** ที่ปรับระดับการช่วยได้ตั้งแต่ทำแทนทั้งหมด
จนถึงเงียบสนิท เป้าหมายคือทำให้คนที่ไม่เคยเป็น DM กล้าเปิดโต๊ะแรก และทำให้ DM ที่เก่งอยู่แล้วทำงานน้อยลง

**ไม่ใช่**: VTT 3D แบบ Foundry/Roll20, rules engine ที่ครบทุก sourcebook, social platform หาเพื่อนเล่น

### โหมดการเล่นที่ต้องรองรับ
| โหมด | อุปกรณ์ | คำอธิบาย |
|---|---|---|
| Online | คนละเครื่อง คนละที่ | แชท + จอแยกของแต่ละคน |
| In-person | คนละแท็บเล็ต + จอกลาง | ล้อมวงจริง, DM พูดเอง, มี table view |
| Companion | เครื่องเดียว (ของ DM) | ผู้เล่นใช้กระดาษ, ระบบเป็นผู้ช่วย DM |
| Quick play | เครื่องใคร เครื่องมัน | ผู้เล่นใส่แค่ชื่อ ไม่ต้องมีบัญชี |

- **ต้องมีอินเทอร์เน็ตเสมอ** — AI คือแกนกลาง ไม่ทำ offline mode
- **Discord** = แค่ช่องเสียง ไม่ทำ bot/embedded app
- **กฎที่รองรับ**: D&D 2024/SRD เป็นฐาน ต่อยอด homebrew ได้ (fully-homebrew mode → เก็บไว้คุย phase ถัดไป)

---

## 2. หลักการสถาปัตยกรรมหลัก

### 2.1 แยก "สมองเล่าเรื่อง" ออกจาก "กติกาเกม" เด็ดขาด

AI orchestrator ไม่แก้ game state โดยตรง แต่**ตัดสินใจเรียก tool** (function calling) เช่น
`roll_dice`, `apply_damage`, `move_token`, `generate_scene_image`, `update_quest_flag`
ตัว **rules engine** (โค้ดปกติ ไม่ใช่ LLM) เป็นคนคำนวณและเขียนลง DB จริง แล้วส่งผลกลับให้ AI
เอาไปแต่งเป็นคำบรรยาย

ประโยชน์: ตัวเลขถูกต้อง 100%, กัน prompt injection จากผู้เล่น, ลด context ที่ต้องส่งเข้า LLM ทุกเทิร์น

### 2.2 Approval gate — AI autonomy เป็น dial ต่อ subsystem

DM ตั้งค่าอิสระต่อระบบย่อย ปรับได้ตลอดเกม:

| Subsystem | Manual | Suggest | Autonomous |
|---|---|---|---|
| Narration | DM พิมพ์/พูดเอง | AI ร่างให้ DM แก้ก่อนส่ง | AI เล่าตรงให้ผู้เล่น |
| NPC dialogue | DM พากย์เอง | AI เสนอบท รอ approve | AI ตอบโต้สด |
| Rules/combat | DM ตัดสินเอง | AI คำนวณให้ดู รอ confirm | rules engine auto-resolve |
| Map/scene | DM อัปโหลด set | — | AI เจนเมื่อไม่มี set เตรียมไว้ |

Tool call ที่อยู่ใน category "suggest" จะไม่ execute ทันที แต่ถูกสร้างเป็น **pending proposal**
ส่งเข้า DM console เท่านั้น (ผู้เล่นยังไม่เห็น) DM กด approve / edit / reject

**กฎตายตัวที่ไม่ขึ้นกับ dial**: AI ไม่มีสิทธิ์สั่ง action ให้ตัวละครผู้เล่นโดยไม่ผ่านการยืนยันจาก
ผู้เล่นเจ้าของตัวละคร ไม่ว่า autonomy จะตั้งเป็นอะไร

### 2.3 Guided vs Custom mode (สำหรับมือใหม่ vs มือโปร)

ไม่ใช่สองระบบแยกกัน แต่เป็น **ค่าตั้งต้น + การซ่อน/แสดง UI** บนสคีมาเดียวกัน สลับไปมาได้โดยข้อมูลไม่หาย

| | Guided (มือใหม่) | Custom (มือโปร) |
|---|---|---|
| สร้าง session | ตอบ wizard 3-4 ข้อ → AI generate outline/NPC/map ให้ | หน้าเปล่าจัดเอง |
| Autonomy dial | ซ่อน ตั้ง auto เกือบทั้งหมด | เห็นครบทุก subsystem |
| Proposal | ไม่มีคิว AI ทำเลย | คิวเต็มรูปแบบ |
| กฎ/dice | auto-resolve เงียบๆ | เห็น roll ดิบ แก้ผลได้ |

สำหรับมือใหม่: AI เป็นแกนให้ดำเนินเกมต่อไปได้
สำหรับ hardcore: AI เป็นผู้ช่วยจัดการส่วนเสริม พร้อมเตือนว่าการปรับแต่ละจุดกระทบส่วนไหนของแคมเปญบ้าง
สรุปด้วยหน้าต่าง custom-AI settings ที่บอกได้ว่าเซสชันจะดำเนินในรูปแบบไหน

**Session wizard**: ถามผู้เล่นกี่คน / บรรยากาศแบบไหน / เล่นนานแค่ไหน → เรียก tool
`generate_campaign_skeleton` สร้าง outline + NPC + map ให้ครบชุด

---

## 3. Data model

```
Campaign (world_state jsonb, mode: guided/custom, rules_system: srd2024)
├─ StoryOutline (beats[]: title, summary, objective, entry_trigger,
│                exit_conditions[], monster_refs[], npc_refs[],
│                secrets[]{text, reveal_condition}, dm_notes, review_status)
├─ Character (sheet jsonb — stats/HP/inventory/spell slots/limited-use)
├─ CharacterResource (character_id, name, current, max,
│                     reset_on: short_rest/long_rest/dawn/never)
├─ Session (mode: online/in_person/companion, status)
│  ├─ Message (author_type, content, tool_calls jsonb)
│  ├─ MapState / Scene (grid, tokens[], fog layer, background_image_url)
│  ├─ Proposal (subsystem, tool_call_json, reason, status: pending/approved/edited/rejected)
│  ├─ RestPhase (type: short/long, hours?, watch_order[], status)
│  │  └─ PlayerRestAction (character_id, hit_dice_spent, abilities_used[], confirmed)
│  └─ RecapSnapshot (session_id, dm_version_text, player_version_text)
├─ ConsultMessage (dm_user_id, role: dm/ai, content, linked_beat_id?)
├─ SourceDocument (owner_user_id, storage_key, ocr_status)  ← private, DM only
│  ├─ RawChunk (vector index, namespace = campaign_id)
│  ├─ MonsterStatBlock (AC, HP, actions[], condition_refs[], review_status)
│  ├─ NpcProfile (motive, voice_hint, knows_secrets[], review_status)
│  └─ MapAsset (crop_source, grid alignment, review_status)
└─ AutonomySetting (subsystem, level: manual/suggest/autonomous)

User
├─ ApiKey (provider, encrypted_key, key_suffix, status)  ← ผูกกับ user ไม่ใช่ campaign
├─ StorageQuota (used_bytes, limit_bytes)  ← per account, share ทั้งบัญชี
└─ CampaignMember (campaign_id, role: dm/player)  ← DM ต้องมีบัญชีเต็มเสมอ
```

---

## 4. DM Console — โครงหน้าจอ

### 4.1 Campaign setup (เตรียมก่อนเล่น / ไม่บ่อย)
- Story outline editor (beats, secrets, NPC)
- Map & token library (อัปโหลด set, tag ตาม scene)
- Session wizard (guided mode)
- AI autonomy default ต่อแคมเปญ
- ตั้งค่า API key (ดู §7)

### 4.2 Live session — 3 โซนหลัก

**ซ้าย — Story outline**: รายการ beat เรียงลำดับ พร้อม flag ผ่านแล้ว/กำลังเล่น/ยังไม่ถึง แก้ไขได้แม้ระหว่างเล่น

**กลาง — Scene/map (DM view เต็ม ไม่มี fog บัง)**: อัปโหลด set หรือกด generate ด้วย AI, ปุ่ม fog control
(deterministic ไม่ผ่าน AI), token tray

**ขวา — แท็บสลับกัน 2 อัน**:
- *AI autonomy + Pending proposals*: dial ต่อ subsystem, คิว proposal พร้อมเหตุผลกำกับทุกอัน
  + ปุ่ม "ไม่แน่ใจ ให้ AI ตัดสิน" สำหรับมือใหม่
- *Ask AI — DM consult*: แชทปรึกษาส่วนตัว แยกขาดจาก proposal queue (คุยได้โดยไม่กระทบ game state
  จนกว่าจะกดปุ่มแปลงเป็น action จริง เช่น "ใส่ใน story outline" / "ส่งเป็น proposal")

### 4.3 Scene prep panel
ผูกกับ beat ที่ active อยู่อัตโนมัติ แสดง: มอนสเตอร์ที่จะเจอ + stat block ย่อ, condition พร้อม tooltip
อธิบายศัพท์, NPC ในฉาก + motive, กล่องเตือน "ผู้เล่นยังไม่ควรรู้" (secrets) แยกสีชัดเจน
อ่านจาก record ที่ `review_status = approved` เท่านั้น

### 4.4 Rest phase control
DM กดเปิด short/long rest → แท็บเล็ตผู้เล่นเด้ง action (เลือก hit dice / ใช้สกิลเฉพาะช่วงพัก)
long rest กรอกจำนวนชั่วโมง + ลำดับเฝ้าเวร (ใช้เป็น narrative context ให้ AI ตัดสิน random encounter ได้ด้วย)
ครบทุกคน → resolve รวดเดียว คืน HP + รีเซ็ต resource ตาม `reset_on`

### 4.5 Table view (จอกลาง, in-person)
Read-only ไม่มี input ใดๆ — แผนที่ + initiative + HP ฝั่งผู้เล่นเท่านั้น (ไม่โชว์ HP ศัตรู) + แถบ "ล่าสุด"
ตัวหนังสือใหญ่อ่านจากระยะไกลได้ ไม่มีข้อความบรรยาย เพราะ DM พูดเอง

---

## 5. Source import pipeline (สแกนหนังสือ → structured data)

```
Scan (PDF/รูป) → vision extraction (ต่อหน้า, JSON out) → merge/dedupe
→ DM review screen → commit เข้า campaign
```

- **ไม่ commit อัตโนมัติ** ต้องผ่านหน้า review เสมอ (ตัวเลข AC/HP ผิดพลาดได้จาก extraction)
- Output แยกเป็น `AdventureBeat` / `MonsterStatBlock` / `NpcProfile` / `MapAsset` / `unresolved[]`
  (รายการที่ AI อ่านไม่ชัด ต้องรายงานแทนการเดา)
- Vision prompt: บรรยาย = สรุปใหม่ด้วยคำตัวเอง (ห้ามคัดลอกย่อหน้า), ตัวเลขกลไก = ต้องตรงเป๊ะ
- **Privacy บังคับใน schema**: `SourceDocument`/`RawChunk` เข้าถึงได้เฉพาะ DM role, filter ด้วย
  `campaign_id` เสมอ, vector index แยก namespace ต่อแคมเปญ, ไฟล์ดิบ signed URL อายุสั้น,
  `delete_campaign_sources` เป็น hard delete, ปิดได้ทั้งระบบด้วย feature flag `ENABLE_SOURCE_IMPORT`
- ทางเลือกเนื้อหาที่ปลอดภัยกว่า: **SRD 5.2 อยู่ภายใต้ CC-BY-4.0** ใช้/แก้/แจกจ่ายได้ฟรี ฝังเป็น
  `RulesCorpus` ในระบบได้เลยตั้งแต่ deploy โดยไม่ต้องพึ่งการสแกน — ใช้เป็นฐานของ preset character
  sheet และ rules engine ได้ทันที
- Official free adventures (Borderlands Quest, Dragons of Stormwreck Isle ฯลฯ) ใช้เป็นเนื้อหา
  ทดสอบระบบได้ดี เป็นไฟล์ดิจิทัลอยู่แล้ว (ข้าม OCR) แต่ยังมีลิขสิทธิ์เต็ม ต้องอยู่ใต้กฎ private เดียวกัน

---

## 6. Recap

- เขียนเป็นไฟล์ต่อแคมเปญ (`RecapSnapshot`) เพราะ DM คนเดียวอาจรันหลายแคมเปญ
- **มีสองเวอร์ชันเสมอ**: DM version (รวม secret ที่ยังไม่เปิด) กับ Player version (เฉพาะสิ่งที่ตัวละคร
  รับรู้จริง) — ใช้กฎเดียวกับ `secrets[]`
- Trigger: ตอน resume session ระบบถามว่าจะ recap หรือไม่ (ไม่ auto)

---

## 7. BYOK — API key ต่อผู้ใช้

- Key ผูกกับ **user ไม่ใช่ campaign** (`ApiKey.user_id`) เผื่ออนาคตผู้เล่นใช้ key ตัวเองได้
- เข้ารหัสด้วย ASP.NET Data Protection API ก่อนเก็บ DB, master key แยกเก็บนอก DB
- Validate ด้วย test call ทันทีตอนกรอก
- Runtime: decrypt ใน memory เฉพาะตอนยิง API เท่านั้น ไม่ log ค่า key เต็มที่ไหนเลย
- UI: โชว์แค่ท้าย 4 หลัก, ปุ่มเปลี่ยน/ลบ, แจ้งชัดว่าเรียกเก็บจากบัญชี Anthropic ของผู้ใช้เอง ไม่ใช่ของแอป
- **Quick play ใช้ key ของ DM เท่านั้น** (ไม่มี user จริงให้ผูก key)
- โมเดลแนะนำ: Claude Sonnet — จุดคุ้มสุดสำหรับงานเล่าเรื่อง + tool call, ใช้ prompt caching กับ
  world state/character sheet ที่ส่งซ้ำทุกเทิร์นเพื่อลดต้นทุน

---

## 8. บัญชีและ storage

- **DM ต้องมีบัญชีเต็มเสมอ** ผู้เล่นเลือกได้ระหว่าง quick play (ใส่ชื่อ เล่นจบ session ก็เคลียร์)
  กับสมัครบัญชีเต็ม
- Quick play: จบ session ถามผู้เล่นแต่ละคนว่าจะเก็บตัวละครไหม → ถ้าไม่ตอบ เก็บแบบ orphan
  พร้อม claim link 7 วัน แล้วค่อย hard delete
- Storage quota เป็น **per account** (แชร์ร่วมกันทั้งบัญชี ไม่ว่าจะเล่นเป็น DM หรือ player)
  แคมเปญที่ถูกลบต้องคืน quota จริง — เผื่อขาย storage เพิ่มในอนาคต

---

## 9. Stack ที่แนะนำ

> **ฉบับเต็มและเป็นทางการอยู่ที่ [`PROJECT-CONTEXT.md`](./PROJECT-CONTEXT.md) §3** (ทบทวน 2026-08-21)
> — รวมการแบ่งโมเดลตามงาน, ฟีเจอร์ Claude API ที่ต้องใช้ และข้อบังคับทางสถาปัตยกรรม
> ส่วนด้านล่างคือแกนหลักโดยย่อ

- Backend: .NET (ASP.NET Core) + SignalR (realtime hub เดียวกันรองรับทั้ง online/async)
- DB: PostgreSQL (JSONB สำหรับ character sheet/world state ที่ schema เปลี่ยนบ่อย) + Redis (presence/pub-sub)
- Frontend: Next.js + Canvas/SVG สำหรับ grid map
- LLM: Claude API พร้อม tool-use, orchestrator เป็น service แยกใน .NET
- Deploy: Docker + Jenkins ตามที่ใช้อยู่แล้ว

> ⚠️ **`generate_scene_image` ใน §2.1 กับการ generate แผนที่ใน §4.2 ยังไม่มีทางทำจริง** —
> Claude API สร้างรูปภาพไม่ได้ ต้องใช้ provider เจ้าอื่น ดู `PROJECT-CONTEXT.md` §4

---

## 10. ยังไม่ได้ตัดสินใจ / เก็บไว้คุยต่อ

- Fully-homebrew mode (นอกกรอบ D&D 2024/SRD) — เก็บไว้ phase ถัดไป
- XP-based leveling — รองรับใน data model แต่ยังไม่ build phase ต้น
- AI ช่วยผู้เล่นหา feat/spell/item + auto-generate build — ไอเดียอนาคต ใช้ API key ของ DM หรือผู้เล่นเองก็ได้
- Pause/resume vote จากผู้เล่น (นอกจากปุ่มฝั่ง DM)

---

## 11. ขั้นถัดไป

ฝั่ง **Player** ยังไม่ได้ออกแบบ — จุดที่ต้องตอบตั้งแต่แรกคือบทบาทของแท็บเล็ตผู้เล่นในโหมด in-person
(ดู character sheet อย่างเดียว หรือประกาศ action ผ่านแท็บเล็ตด้วย) และต้องออกแบบให้ครอบคลุมทั้ง 4 โหมด
การเล่น (online / in-person / companion / quick play) ตั้งแต่ต้น

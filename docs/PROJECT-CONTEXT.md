# DnDEz — Project context (handoff)

> เอกสารนี้คือ **primer สำหรับเริ่มแชทใหม่** (Cowork, Claude Code, หรือแชทออกแบบต่อ)
> อ่านไฟล์นี้ก่อน แล้วค่อยเปิด [`dm-side-design.md`](./dm-side-design.md) /
> [`player-side-design.md`](./player-side-design.md) ถ้าต้องการรายละเอียดเต็ม
>
> **สถานะ**: ฝั่ง DM ออกแบบเสร็จแล้ว · ฝั่ง Player ออกแบบรอบแรกเสร็จแล้ว (2026-08-21) · ยังไม่เขียนโค้ด

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

| ส่วน | เลือกใช้ |
|---|---|
| Backend | .NET (ASP.NET Core) + SignalR |
| DB | PostgreSQL (JSONB) + Redis |
| Frontend | Next.js + Canvas/SVG (grid map) |
| LLM | Claude API (แนะนำ Sonnet) + prompt caching |
| Deploy | Docker + Jenkins |

---

## 4. สิ่งที่ยังไม่ตัดสินใจ / ยังไม่ออกแบบ

**ฝั่ง Player — ออกแบบรอบแรกเสร็จแล้ว** (2026-08-21)
คำถามเปิดเดิม "แท็บเล็ตผู้เล่นดู sheet อย่างเดียวหรือประกาศ action ได้ด้วย" → **ตอบแล้ว: ประกาศ action ได้**
เหลือ 2 จุดที่รอ feedback จากการใช้จริง
- ภาระ DM ที่ต้องกรอกผลเต๋าให้ผู้เล่นทุกคนในโหมด in-person (`player-side-design.md` §5)
- การส่ง panel แนะนำ action ในโหมด online ที่ DM ไม่ได้พูดสด (`player-side-design.md` §7)

**ยังไม่ออกแบบ**: หน้าจอ onboarding/ตั้งค่าแรกเข้า · flow การจ่ายเงิน/แพ็กเกจ storage ·
รายละเอียด UI ของ table view

**เก็บไว้ phase ถัดไป**
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

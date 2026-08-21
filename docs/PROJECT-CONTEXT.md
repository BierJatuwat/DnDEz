# DnDEz — Project context (handoff)

> เอกสารนี้คือ **primer สำหรับเริ่มแชทใหม่** (Cowork, Claude Code, หรือแชทออกแบบต่อ)
> อ่านไฟล์นี้ก่อน แล้วค่อยเปิด [`dm-side-design.md`](./dm-side-design.md) ถ้าต้องการรายละเอียดเต็ม
>
> **สถานะ**: ฝั่ง DM ออกแบบเสร็จแล้ว · ฝั่ง Player ยังไม่เริ่ม · ยังไม่เขียนโค้ด

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

**ขั้นถัดไปทันที — ฝั่ง Player** (ยังไม่เริ่มเลย)
คำถามแรกที่ต้องตอบ: แท็บเล็ตผู้เล่นในโหมด in-person ทำอะไรได้บ้าง — ดู character sheet
อย่างเดียว หรือประกาศ action ผ่านแท็บเล็ตด้วย (ถ้าได้ จะลดภาระการสรุปของ DM ลงมาก)
และต้องออกแบบให้ครอบคลุมทั้ง 4 โหมดตั้งแต่แรก

**เก็บไว้ phase ถัดไป**
- Fully-homebrew mode (ออกนอกกรอบ D&D 2024/SRD ทั้งหมด)
  → ข้อควรระวังที่คุยไว้: ถ้า homebrew เปลี่ยนแกน d20/AC/HP → rules engine ใช้ไม่ได้
    แนะนำจำกัดให้ใช้แกน d20 เสมอ เปลี่ยนได้แค่เนื้อหา
- XP-based leveling (รองรับใน data model แต่ยังไม่ build)
- AI ช่วยผู้เล่นหา feat/spell/item + auto-generate build
- Pause/resume vote จากฝั่งผู้เล่น

---

## 5. วิธีใช้เอกสารนี้ในแชทใหม่

**สำหรับแชทออกแบบต่อ**: อ่านไฟล์นี้ + `dm-side-design.md` แล้วบอกว่าจะออกแบบส่วนไหนต่อ

**สำหรับ Claude Code / งานเขียนโค้ด**: ยึด §2 (การตัดสินใจที่ล็อก) กับ §3 (stack) เป็นข้อบังคับ
data model เต็มอยู่ใน `dm-side-design.md` §3

**เมื่อมีการตัดสินใจใหม่**: อัปเดตไฟล์นี้ (§2 หรือ §4) แล้ว commit ทุกครั้ง
เพื่อให้ทุกแชทเห็นสถานะเดียวกัน — ไฟล์นี้คือ single source of truth ของบริบทโปรเจกต์

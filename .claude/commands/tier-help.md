---
description: อธิบายว่าระบบ tier agent คืออะไรและใช้งานยังไง
---

แสดงคำอธิบายระบบ tier agent ต่อไปนี้ให้ผู้ใช้ โดยไม่ต้องทำงานอื่นเพิ่ม:

# ระบบ Tier Agent — วิธีใช้

## เปิด/ปิด
- **ค่าเริ่มต้น: ปิด** — พิมพ์คำสั่งปกติ = ทำงานตรงๆ ไม่มี agent ตัวไหนถูกเรียก
- **เปิด:** ขึ้นต้น prompt ด้วย `<tier>` เท่านั้น จึงจะเข้าสู่ระบบ routing

ตัวอย่าง: `<tier> สร้าง endpoint อัปเดตโปรไฟล์ พร้อม validation และ unit test`

## เมื่อเปิด `<tier>` ระบบจะทำอะไร
1. **Decision framework** ประเมินก่อนว่างานควรหั่นไหม — ถ้าเป็นงานคิดเส้นเดียว (อธิบาย, เขียนต่อเนื่อง) จะทำเองรวดเดียว ไม่ route แม้เปิด tier ไว้
2. ถ้างานแตกเป็นส่วนอิสระได้ → เข้าระบบ routing ตามลำดับความยาก

## ลำดับ agent (haiku < sonnet < opus < fable)
- **sonnet-worker** — first triage รับงานทุกชิ้นก่อน + งานเหตุผลปานกลาง
- **haiku-worker** — งานง่าย ทำซ้ำ (format, extract, boilerplate)
- **opus-worker** — owner: วางโครงสร้าง แตก subtask เขียนเทสต์ (red) แล้ว dispatch การเขียนโค้ดออก
- **fable-worker** — งานซับซ้อนสูงสุด เพดานบนของระบบ
- **code-reviewer** — ตรวจคุณภาพโค้ด + คุณภาพเทสต์ (จำกัดแก้ 2 รอบ)
- **security-reviewer** — เรียกเฉพาะเมื่อแตะ security surface (auth, external input, DB query, secrets, network/file I/O, dependency ใหม่)

## หลักการที่ยึด
- **Escalate ขึ้น / Dispatch ลง** — worker ส่งขึ้นเมื่อเกินมือ, owner แตกงานลงได้ในฐานะเจ้าของ
- **แยก blind spot** — คนเขียนเทสต์ ≠ คนเขียนโค้ด, คนเขียนโค้ด ≠ คนรีวิว
- **TDD** เฉพาะงานมี logic (ยกเว้น pure CRUD), ตรวจ test framework จากโปรเจกต์เอง

## ⚠️ ข้อควรรู้
กฎการ escalate/route ทั้งหมดเป็น **convention (คำสั่งในพรอมต์)** ไม่ใช่ hard restriction ที่ระบบบังคับ — Claude Code ไม่จำกัดว่า agent เรียกใครได้จาก frontmatter ถ้าต้องการบังคับจริงต้องตั้ง `permissions.deny` ที่ project settings

## ดู log
เปิด `.claude/logs/routing.md` เพื่อดูว่ามีการ escalate/review เกิดขึ้นตามกฎจริงไหม ใช้ข้อมูลนี้จูนเกณฑ์ใน CLAUDE.md

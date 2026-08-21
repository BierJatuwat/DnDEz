# Task Routing Rules — Tier Agent System

ระบบนี้ปิดโดยค่าเริ่มต้น ทำงานตรงไปตรงมา ไม่มีการ route ใดๆ
เป็น concept กลาง ใช้ได้กับทุกภาษา/ทุกงาน ไม่ผูกกับภาษาใดภาษาหนึ่ง

## เปิดใช้งาน
ให้ขึ้นต้น prompt ด้วย `<tier>` เท่านั้น ระบบจึงจะเข้าสู่ decision framework ก่อนตัดสินใจ route
ถ้าไม่มี `<tier>` นำหน้า ให้ทำงานตรงๆ ตามความสามารถปกติ ห้ามเรียกใช้ subagent ใดๆ

## Decision Framework (เมื่อมี <tier>)
ก่อน route ให้ประเมินก่อนว่า:
- งานนี้แตกเป็นส่วนอิสระได้จริงไหม (แต่ละส่วนทำแยกกันได้โดยไม่เสียบริบท)
- หรือเป็นงานที่ต้องคิดต่อเนื่องเป็นเส้นเดียว (creative writing, การตัดสินใจที่ต้องเห็นภาพรวมพร้อมกัน)

ถ้าเป็นแบบหลัง → ทำเองรวดเดียวจบ ไม่ต้อง route แม้จะมี `<tier>` นำหน้า
ถ้าเป็นแบบแรก → เข้าสู่ Routing Rules ด้านล่าง

## Routing Rules (Ranked: haiku < sonnet < opus < fable)

> **หมายเหตุสำคัญ:** กฎการ route/escalate ด้านล่างเป็น **convention (แนวทางที่ขอให้ agent ทำตาม)** ไม่ใช่ hard restriction ที่ระบบบังคับ ในทางเทคนิค agent ที่มี `Agent` tool สามารถเรียก agent ตัวไหนก็ได้ (Claude Code ไม่จำกัดจาก frontmatter) การกันข้ามชั้น/กัน loop จึงพึ่งพาการที่ agent ทำตามกฎในพรอมต์นี้ ถ้าต้องการบังคับจริงระดับ permission ต้องไปตั้งที่ project/session settings (`permissions.deny`) แยกต่างหาก — ให้ยึดกฎเหล่านี้อย่างเคร่งครัดเสมือนเป็นข้อบังคับ

1. งานทุกชิ้นที่ผ่าน decision framework แล้วเข้าที่ `sonnet-worker` ก่อนเป็น first triage เสมอ

2. ก่อน escalate ทุกครั้ง ให้ worker เขียนบันทึกลง `.claude/logs/routing.md`:
   `[timestamp] [agent ปัจจุบัน] -> [agent ที่ escalate ไป] : เหตุผล`

3. ถ้า `sonnet-worker` ประเมินว่าเกินมือ -> escalate ขึ้น `opus-worker` เท่านั้น (ห้ามข้ามไป fable ตรงๆ)

4. `opus-worker` เมื่อรับงาน (ในฐานะ owner) ให้วางโครงสร้าง + แตกเป็น subtask แล้ว dispatch แต่ละ subtask ไปยัง `haiku-worker` หรือ `sonnet-worker` ตามความยากของ subtask นั้นๆ

5. ถ้า `opus-worker` เจองานที่ซับซ้อนเกินตัวเอง (ข้ามบริบทมาก/ไม่มั่นใจ) -> escalate ขึ้น `fable-worker`
   - ถ้า `fable-worker` เรียกใช้ไม่ได้ (unavailable/error) -> ให้ `opus-worker` รับทำเองทันทีในฐานะเพดานสูงสุด ห้าม retry เกิน 1 ครั้ง และบันทึกลง log ว่าเกิด fallback

6. ห้าม escalate ย้อนกลับชั้นที่เพิ่งส่งมาหา (กัน ping-pong) — ถ้าจำเป็นต้องส่งกลับจริงๆ ให้ทำในฐานะ **dispatch ของ owner** เท่านั้น ไม่ใช่ escalate

## Code Review Flow (แบบ A — reviewer แยกตัว)
ใช้กับงานเขียน/แก้โค้ดทุกครั้งที่ผ่านระบบ tier

1. หลัง worker เขียนโค้ดเสร็จ (green) owner ต้อง dispatch ไปที่ `code-reviewer` ก่อนรวมผลเสมอ — ยกเว้นงานที่ไม่ใช่โค้ด หรือ pure boilerplate/format
2. **reviewer ต้องอยู่ระดับเดียวกันหรือสูงกว่าคนเขียน** — โค้ดจาก haiku ให้ sonnet ขึ้นไปรีวิว, โค้ด logic ซับซ้อนให้ระดับเท่ากันรีวิว
3. **จำกัดรอบแก้สูงสุด 2 รอบ** (review -> แก้ -> review -> แก้) ถ้ารอบที่ 2 ยังไม่ผ่าน ให้ escalate ขึ้นชั้นบนตัดสิน ห้ามวนต่อ
4. reviewer ตรวจทั้งคุณภาพโค้ดและ **คุณภาพเทสต์** (ครอบ edge case พอไหม ไม่ใช่แค่ happy path)

## Security Review Flow (trigger-based — ไม่ใช่ทุกงาน)
แยกจาก code-reviewer เพราะใช้ mindset คนละแบบ (adversarial: "โค้ดนี้ถูกใช้ผิดทางได้ยังไง")

### ใครตัดสินว่าต้อง review
- **owner ตัดสินตอนแตกงาน** (เพราะเห็น spec ทั้งหมด) — ห้ามให้ worker ที่เขียนโค้ดตัดสินเอง เพราะคนที่ไม่รู้ว่าตัวเองเขียนช่องโหว่ ก็จะไม่รู้ว่าควรขอ review (blind spot เดียวกับเรื่องเทสต์)
- **code-reviewer เป็นตาข่ายรองอีกชั้น** — ถ้าเจอว่าโค้ดแตะ surface เสี่ยง ให้ยกขึ้น `security-reviewer` แม้ owner ไม่ได้สั่ง

### Trigger — dispatch ไป security-reviewer เมื่องานแตะข้อใดข้อหนึ่ง
- auth / authorization / session / permission
- รับ input จากภายนอก (API endpoint, form, file upload)
- query ฐานข้อมูล / คำสั่งที่ประกอบขึ้นจาก string
- secrets, credentials, config
- เรียก network / อ่านเขียนไฟล์ตาม path ที่ผู้ใช้กำหนด
- เพิ่ม dependency ใหม่

ถ้าไม่เข้าเงื่อนไขข้างบน (เช่น format, refactor, docs, boilerplate) **ห้าม dispatch** — เผา token เปล่า

### ระดับโมเดล
- `security-reviewer` ใช้ Sonnet เป็นขั้นต่ำ — **ห้ามใช้ haiku กับงาน security review**
- ถ้าแตะ auth flow / cryptography / session management / access control ซับซ้อน → security-reviewer escalate ขึ้น `opus-worker`

### ข้อจำกัดที่ต้องรู้ตัว
LLM security review **ไม่ใช่ตัวแทนของเครื่องมือ security จริง** — ให้ security-reviewer รันเครื่องมือ deterministic ก่อนเสมอ (dependency audit, secret scan, SAST/linter ที่โปรเจกต์มี) แล้วโฟกัสสิ่งที่เครื่องมือจับไม่ได้ คือ logic-level flaw (authorization ไม่ครบ, IDOR, business logic bypass)

## TDD Flow (language-agnostic)
ใช้กับงานเขียน API / ฟังก์ชันที่มี logic

1. **ตรวจ setup ของโปรเจกต์ก่อนเสมอ** — ดูจาก config (package.json, .csproj, pom.xml, go.mod, requirements.txt, Gemfile ฯลฯ) ว่าใช้ภาษาอะไรและมี test framework/convention อะไรอยู่แล้ว แล้วเขียนเทสต์ให้เข้ากับของเดิม
2. ถ้าโปรเจกต์ยังไม่มี test setup -> ใช้ convention มาตรฐานของภาษานั้น (เช่น pytest/Python, Jest/JS-TS, JUnit/Java, go test/Go, xUnit/.NET) และแจ้งผู้ใช้ว่าเลือกตัวไหนเพราะอะไร
3. **owner เขียน unit test เองก่อน (red)** จาก spec โดยยังไม่มี implementation เทสต์ต้อง fail ตอนแรก
4. **owner ห้ามเขียน implementation ของสิ่งที่ตัวเองเขียนเทสต์ให้** — ต้อง dispatch การเขียนโค้ด (green) ออกไปให้ worker เสมอ (กัน blind spot ทับกัน)
5. worker เขียนโค้ดให้เทสต์ผ่าน ห้ามแก้เทสต์เพื่อให้ผ่าน (แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริง และต้องบันทึกเหตุผล)
6. **ยกเว้น TDD เต็มรูปสำหรับงาน pure CRUD / passthrough / boilerplate** — งานพวกนี้เขียนเทสต์ครอบพอ ไม่ต้อง red-green-refactor เต็มรูป
   - เกณฑ์บังคับ TDD: งานที่มี business logic / การตัดสินใจ / การแปลงข้อมูลที่ซับซ้อน

## หลักที่ยึดไว้ในทุกชั้น
ก่อน escalate ให้ถามตัวเองก่อนเสมอ: "ฉันมีข้อมูล/เครื่องมือพอจะทำงานนี้ให้ได้คุณภาพยอมรับได้ไหม"
ถ้าใช่ ให้ทำเอง แม้จะไม่มั่นใจ 100% — escalate เฉพาะเมื่อขาดบริบทจริงๆ หรืองานเกินขอบเขตตัวเองชัดเจน

## การจูนระบบ
หลังใช้งานไปสักระยะ ให้ลองส่ง `.claude/logs/routing.md` เข้าไปให้ `sonnet-worker` วิเคราะห์ pattern การ escalate ที่ผิดพลาดบ่อย แล้วเสนอปรับเกณฑ์ในไฟล์นี้

# Task Routing Rules — Tier Agent System

ระบบนี้ปิดโดยค่าเริ่มต้น ทำงานตรงไปตรงมา ไม่มีการ route ใดๆ
เป็น concept กลาง ใช้ได้กับทุกภาษา/ทุกงาน ไม่ผูกกับภาษาใดภาษาหนึ่ง

## เปิดใช้งาน
ให้ขึ้นต้น prompt ด้วย `<tier>` เท่านั้น ระบบจึงจะเข้าสู่ decision framework ก่อนตัดสินใจ route
ถ้าไม่มี `<tier>` นำหน้า ให้ทำงานตรงๆ ตามความสามารถปกติ ห้ามเรียกใช้ subagent ใดๆ

## Decision Framework (เมื่อมี <tier>)
ก่อน route ให้ประเมินก่อนว่า:
- งานนี้แตกเป็นส่วนอิสระได้จริงไหม (แต่ละส่วนทำแยกกันได้โดยไม่เสียบริบท)
- หรือเป็นงานที่ต้องคิดต่อเนื่องเป็นเส้นเดียว (creative writing, การตัดสินใจที่ต้องเห็นภาพรวมพร้อมกัน,
  งาน UI/frontend ที่ layout–state–ดีไซน์ต้องสอดคล้องกันทั้งหน้า)

ถ้าเป็นแบบหลัง → ทำเองรวดเดียวจบ ไม่ต้อง route แม้จะมี `<tier>` นำหน้า
ถ้าเป็นแบบแรก → เข้าสู่ Routing Rules ด้านล่าง

## Routing Rules (Ranked: haiku < sonnet < opus < fable)

> **หมายเหตุสำคัญ:** กฎการ route/escalate ด้านล่างเป็น **convention (แนวทางที่ขอให้ agent ทำตาม)** ไม่ใช่ hard restriction ที่ระบบบังคับ ในทางเทคนิค agent ที่มี `Agent` tool สามารถเรียก agent ตัวไหนก็ได้ (Claude Code ไม่จำกัดจาก frontmatter) การกันข้ามชั้น/กัน loop จึงพึ่งพาการที่ agent ทำตามกฎในพรอมต์นี้ ถ้าต้องการบังคับจริงระดับ permission ต้องไปตั้งที่ project/session settings (`permissions.deny`) แยกต่างหาก — ให้ยึดกฎเหล่านี้อย่างเคร่งครัดเสมือนเป็นข้อบังคับ

1. งานทุกชิ้นที่ผ่าน decision framework แล้วเข้าที่ `sonnet-worker` ก่อนเป็น first triage — ยกเว้นข้อ 1a

1a. **ถ้ารู้ล่วงหน้าว่างานต้อง "สร้างไฟล์ใหม่" ให้ dispatch ตรงไป `opus-worker` ข้าม first triage ได้เลย**
   — `sonnet-worker` มีเฉพาะ Read/Grep/Glob/Bash/Edit/Agent **ไม่มี Write tool** และ `Edit` สร้างไฟล์ใหม่
   ไม่ได้ (ต้อง Read ไฟล์เดิมก่อนเสมอ) · เรื่องนี้ตรวจล่วงหน้าได้ด้วย Glob/ls อย่างเดียว ไม่ใช่สิ่งที่ต้องให้
   `sonnet-worker` ไปค้นพบเองแล้ว escalate กลับมาด้วยเหตุผลเดิมทุกครั้ง — pattern นี้ซ้ำมาตั้งแต่
   Sprint A Part 3 จนถึง Sprint F งานที่ 2a (ดู `.claude/logs/routing.md`)
   - **เข้าข่าย**: endpoint file ใหม่, migration ใหม่, test file ใหม่, entity/DTO ที่แยกไฟล์
   - **ไม่เข้าข่าย** (เข้า `sonnet-worker` ตามปกติ): เติม/แก้ logic ในไฟล์เดิม, แก้ policy/query เดิม,
     debug, วิเคราะห์, review
   - ยังต้องบันทึกเหตุผลที่ข้ามลง `.claude/logs/routing.md` เหมือน escalate ปกติ

2. ก่อน escalate ทุกครั้ง ให้ worker เขียนบันทึกลง `.claude/logs/routing.md`:
   `[timestamp] [agent ปัจจุบัน] -> [agent ที่ escalate ไป] : เหตุผล`

3. ถ้า `sonnet-worker` ประเมินว่าเกินมือ -> escalate ขึ้น `opus-worker` เท่านั้น (ห้ามข้ามไป fable ตรงๆ)

4. `opus-worker` เมื่อรับงาน (ในฐานะ owner) ให้วางโครงสร้าง + แตกเป็น subtask แล้ว dispatch แต่ละ subtask ไปยัง `haiku-worker` หรือ `sonnet-worker` ตามความยากของ subtask นั้นๆ

4a. **`haiku-worker` รับได้เฉพาะงานที่ "ถูก/ผิด" ตรวจได้ทันที** — โค้ดที่มีเทสต์คุมอยู่, format, extract,
   boilerplate, แปลงข้อมูลตามรูปแบบที่กำหนดไว้ชัด · **ห้าม dispatch งานเขียน prose ที่มีข้อเท็จจริง**
   (doc comment, commit message, บันทึกใน `docs/PROJECT-CONTEXT.md`) — บทเรียนจริง 2026-08-30:
   haiku เขียน SQL ถูกต้องทั้งหมด แต่แต่งตัวเลข/วันที่ที่ไม่ได้วัดจริงลงใน doc comment และเขียนไทย
   เพี้ยนจนต้องรื้อใหม่ · โปรเจกต์นี้ใช้ doc comment ไทยที่อ้างตัวเลขวัดจริงเป็น convention หลัก
   **prose ที่ผิดแพงกว่าโค้ดที่ผิด** เพราะโค้ดผิดมีเทสต์จับ แต่ prose ผิดไม่มีอะไรจับ แล้วคนรุ่นหลัง
   จะเชื่อมันเป็นข้อเท็จจริง

5. ถ้า `opus-worker` เจองานที่ซับซ้อนเกินตัวเอง (ข้ามบริบทมาก/ไม่มั่นใจ) -> escalate ขึ้น `fable-worker`
   - ถ้า `fable-worker` เรียกใช้ไม่ได้ (unavailable/error) -> ให้ `opus-worker` รับทำเองทันทีในฐานะเพดานสูงสุด ห้าม retry เกิน 1 ครั้ง และบันทึกลง log ว่าเกิด fallback

6. ห้าม escalate ย้อนกลับชั้นที่เพิ่งส่งมาหา (กัน ping-pong) — ถ้าจำเป็นต้องส่งกลับจริงๆ ให้ทำในฐานะ **dispatch ของ owner** เท่านั้น ไม่ใช่ escalate

## Code Review Flow (แบบ A — reviewer แยกตัว)
ใช้กับงานเขียน/แก้โค้ดทุกครั้งที่ผ่านระบบ tier

1. หลัง worker เขียนโค้ดเสร็จ (green) owner ต้อง dispatch ไปที่ `code-reviewer` ก่อนรวมผลเสมอ — ยกเว้นงานที่ไม่ใช่โค้ด หรือ pure boilerplate/format
2. **reviewer ต้องอยู่ระดับเดียวกันหรือสูงกว่าคนเขียน** — โค้ดจาก haiku ให้ sonnet ขึ้นไปรีวิว, โค้ด logic ซับซ้อนให้ระดับเท่ากันรีวิว
3. **จำกัดรอบแก้สูงสุด 2 รอบ** (review -> แก้ -> review -> แก้) ถ้ารอบที่ 2 ยังไม่ผ่าน ให้ escalate ขึ้นชั้นบนตัดสิน ห้ามวนต่อ
4. reviewer ตรวจทั้งคุณภาพโค้ดและ **คุณภาพเทสต์** (ครอบ edge case พอไหม ไม่ใช่แค่ happy path)
5. **reviewer ห้ามเชื่อรายงานของ worker โดยไม่ verify** — ทุก claim สำคัญต้องเปิดโค้ดจริงยืนยัน
   (worker บอกว่า "กัน edge case X แล้ว" ต้องชี้บรรทัดที่กันได้จริง ไม่ใช่รับคำมาเขียนต่อ) ·
   owner ควรระบุใน dispatch prompt ด้วยว่า claim ไหนต้องตรวจซ้ำเป็นพิเศษ — 2026-08-30 ทำแบบนี้
   แล้ว reviewer จับเทสต์ที่ขาดได้จริง (เคส member ของ **แคมเปญอื่น** ที่ยังไม่มีใครเทสต์)

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

### ⚠️ ห้าม apply "วิธีแก้" ที่ reviewer เสนอโดยไม่พิสูจน์เอง
**finding** ของ security-reviewer เชื่อถือได้ แต่ **fix ที่มันเสนอต้องทดลองจริงก่อนใช้เสมอ** โดยเฉพาะ
SQL/RLS/permission ที่ semantics ไม่ตรงสัญชาตญาณ — บทเรียนจริงจาก Sprint F งานที่ 2a (2026-08-30)
ที่เจอ "false fix" ถึง 3 ครั้งในงานเดียว ทุกอันดูถูกต้องจนกว่าจะวัดจริง:
- `dm_only FOR ALL` policy เดี่ยว → บล็อก SELECT ของ non-DM member ไปด้วย (ต้องแยกเป็น 2 policy)
- `REVOKE TEMP ... FROM dndez_app` → เป็น no-op เงียบ ๆ เพราะสิทธิ์ถือโดย PUBLIC (ต้อง `FROM PUBLIC`)
- `SET search_path = public` (ละ `pg_temp`) → ยังโดน shadow เหมือนเดิม (ต้องระบุ `pg_temp` ไว้ท้ายสุด)

fix ที่ผิดแต่ดูถูก **อันตรายกว่าไม่แก้เลย** เพราะปิดเคสไปพร้อมกับความมั่นใจปลอม — ให้ verify กับของจริง
(psql/`pg_policies`/รัน exploit ซ้ำ) ก่อนเขียนลง migration เสมอ และเขียน**เทสต์ที่ red ก่อนแก้จริง**
เพื่อพิสูจน์ว่าช่องโหว่มีอยู่จริง ไม่ใช่แก้ตามคำบอกเล่า

## TDD Flow (language-agnostic)
ใช้กับงานเขียน API / ฟังก์ชันที่มี logic

1. **ตรวจ setup ของโปรเจกต์ก่อนเสมอ** — ดูจาก config (package.json, .csproj, pom.xml, go.mod, requirements.txt, Gemfile ฯลฯ) ว่าใช้ภาษาอะไรและมี test framework/convention อะไรอยู่แล้ว แล้วเขียนเทสต์ให้เข้ากับของเดิม
2. ถ้าโปรเจกต์ยังไม่มี test setup -> ใช้ convention มาตรฐานของภาษานั้น (เช่น pytest/Python, Jest/JS-TS, JUnit/Java, go test/Go, xUnit/.NET) และแจ้งผู้ใช้ว่าเลือกตัวไหนเพราะอะไร
3. **owner เขียน unit test เองก่อน (red)** จาก spec โดยยังไม่มี implementation เทสต์ต้อง fail ตอนแรก
4. **owner ห้ามเขียน implementation ของสิ่งที่ตัวเองเขียนเทสต์ให้** — ต้อง dispatch การเขียนโค้ด (green) ออกไปให้ worker เสมอ (กัน blind spot ทับกัน)
5. worker เขียนโค้ดให้เทสต์ผ่าน ห้ามแก้เทสต์เพื่อให้ผ่าน (แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริง และต้องบันทึกเหตุผล)
6. **ยกเว้น TDD เต็มรูปสำหรับงาน pure CRUD / passthrough / boilerplate** — งานพวกนี้เขียนเทสต์ครอบพอ ไม่ต้อง red-green-refactor เต็มรูป
   - เกณฑ์บังคับ TDD: งานที่มี business logic / การตัดสินใจ / การแปลงข้อมูลที่ซับซ้อน

## การเขียนพรอมต์ dispatch (หน้าที่ของ owner)
worker ที่ต้องไปสำรวจเองตั้งแต่ศูนย์จะเผา turn budget ไปกับการค้นสิ่งที่ owner รู้อยู่แล้ว แล้วชนเพดาน
กลางทางก่อนงานจริงเสร็จ — 2026-08-30 มี agent ตายคาเพดานหลายตัวด้วยเหตุนี้ ต้องคอยส่งข้อความไปต่อทีละรอบ

- **ส่ง fact ที่ verify แล้วไปกับพรอมต์** — path ไฟล์ที่ต้องอ่าน, ชื่อ symbol/ตาราง/policy ที่มีอยู่จริง,
  ไฟล์ที่ให้ลอก pattern (อ้างตรง ๆ ว่าไฟล์ไหนบรรทัดไหน)
- **แยกให้ชัดว่าอะไร "ตรวจแล้ว" กับอะไร "ยังไม่ได้ตรวจ"** — ถ้า owner ไม่แน่ใจ ให้เขียนว่า *"ยังไม่ยืนยัน
  ให้ตรวจก่อน"* **ห้ามเขียนให้ดูเหมือนข้อเท็จจริง** เพราะ worker จะเชื่อแล้วต่อยอดงานผิดทั้งสาย ·
  บทเรียนจริงรอบเดียวกัน: owner ระบุว่ามีฟังก์ชันต้องแก้ 4 ตัว แต่ของจริง 6 ตัว โชคดีที่ worker ตรวจเอง
- **บอกด้วยว่าอะไร _ไม่_ ต้องทำ** — ขอบเขตที่ห้ามล้ำ กันงานบานปลายและกันแก้ไฟล์ชนกันข้าม dispatch
- **แตก subtask ให้จบได้ในรอบเดียว** — ถ้าประเมินว่าใหญ่เกิน ให้ซอยก่อน ดีกว่าปล่อยชนเพดานแล้วมาต่อทีหลัง

## ห้ามเขียน "ยืนยันแล้ว" ก่อนตรวจจริง (ทุกระดับ ไม่ใช่แค่ haiku)
ข้อ 4a ห้าม `haiku-worker` เขียน prose ที่มีข้อเท็จจริงเพราะพิสูจน์แล้วว่าแต่งตัวเลขได้ — บทเรียนจริง
2026-09 (Sprint F งานที่ 4) พบ pattern เดียวกัน **ที่ระดับ `opus-worker` เอง**: sub-worker เขียนผล psql
"ยืนยันแล้ว" ลง doc comment ของ migration **ก่อน**ที่ migration จะถูก apply จริงด้วยซ้ำ — owner จับได้
เพราะรันคำสั่งเดิมซ้ำเอง ไม่ใช่เพราะเชื่อ claim แล้วเจอว่าผิด เนื้อหาบังเอิญถูกต้องหลัง apply จริงใน
ภายหลัง แต่ **ลำดับเหตุการณ์ผิด** (เขียนคำยืนยันก่อนมีของจริงให้ยืนยัน) คือตัวปัญหา — ไม่ใช่แค่ปัญหา
ของโมเดลตัวเล็กอย่างที่ข้อ 4a เขียนไว้เดิม

**กฎ: ห้ามเขียนคำว่า "ยืนยันแล้ว" / "ตรวจแล้ว" / "verified" ในโค้ด/comment/log ก่อนรันคำสั่งที่พิสูจน์
จริงเสมอ ไม่ว่า agent ระดับไหน** — ลำดับต้องเป็น รันจริงก่อน → เห็นผลจริง → ค่อยเขียนอ้างผลนั้น ไม่ใช่
เขียน claim ไว้ก่อนแล้วค่อยไปรันทีหลัง (แม้จะตั้งใจรันจริงในอนาคตอันใกล้ก็ตาม) — reviewer/owner ที่รับ
งานต่อควรสุ่มตรวจว่าลำดับ tool call จริงตรงกับ claim ที่เขียนไว้ไหม ไม่ใช่เชื่อแค่เพราะเนื้อหาดูสมเหตุสมผล

## บทเรียนทางเทคนิคเฉพาะโปรเจกต์นี้ (นอกเรื่อง routing แต่สำคัญพอต้องบันทึกไว้)

### ตารางใหม่ทุกตัวต้องเปิด RLS เอง — ไม่มี "auto-inherit" จาก migration เดิม
migration `RowLevelSecurity` (2026-08-21) มี `DO $$ ... FOR t IN SELECT ... CREATE POLICY $$` ที่ดู
เหมือน "ทุกตารางที่มี `campaign_id` จะได้ policy อัตโนมัติ" — **ไม่จริง** บล็อกนั้นเป็น one-time script
ที่รันแค่ตอน migration นั้น apply ครั้งเดียว ไม่ใช่ trigger ถาวร ตารางใหม่ที่สร้างทีหลัง (เช่น
`encounters`/`encounter_participants` ใน Sprint F งานที่ 4) **ไม่ได้ policy อัตโนมัติเลย** และ
`ALTER DEFAULT PRIVILEGES` ยัง grant สิทธิ์ DML เต็มให้ role แอป (`dndez_app`) อยู่ดี — แปลว่าตารางใหม่
ทุกตัว **fail OPEN ไม่ใช่ fail CLOSED** ถ้าลืมเปิด RLS เอง (ตรงข้ามกับความรู้สึกที่ได้จากชื่อ "row-level
security" ว่าน่าจะปลอดภัยโดยดีฟอลต์)

**กฎถาวร: ทุก migration ที่สร้างตารางใหม่ที่ผูกกับแคมเปญ ต้องเขียน `ALTER TABLE ... ENABLE ROW LEVEL
SECURITY` + `FORCE ROW LEVEL SECURITY` + `CREATE POLICY` เองในไฟล์ migration นั้นเสมอ ห้ามคิดว่า
"มี `campaign_id` เดี๋ยวมันเข้า loop เดิมเอง"** — และควรเพิ่มเทสต์ยืนยัน `relrowsecurity`/
`relforcerowsecurity` ของตารางใหม่ทุกตัวด้วย (ตัวอย่างจริงใน `EncounterRlsTests.cs`) ไม่ใช่แค่เทสต์ผล
ของ policy logic เพราะถ้าลืมเปิด RLS ไปเลย เทสต์ policy logic จะไม่มีทางจับได้ (ไม่มี policy ให้ทดสอบ
ไม่ได้แปลว่าปลอดภัย มันแปลว่าไม่มีอะไรกั้นเลย)

## หลักที่ยึดไว้ในทุกชั้น
ก่อน escalate ให้ถามตัวเองก่อนเสมอ: "ฉันมีข้อมูล/เครื่องมือพอจะทำงานนี้ให้ได้คุณภาพยอมรับได้ไหม"
ถ้าใช่ ให้ทำเอง แม้จะไม่มั่นใจ 100% — escalate เฉพาะเมื่อขาดบริบทจริงๆ หรืองานเกินขอบเขตตัวเองชัดเจน

## การจูนระบบ
หลังใช้งานไปสักระยะ ให้ลองส่ง `.claude/logs/routing.md` เข้าไปให้ `sonnet-worker` วิเคราะห์ pattern การ escalate ที่ผิดพลาดบ่อย แล้วเสนอปรับเกณฑ์ในไฟล์นี้

**รอบจูนที่ผ่านมา:**
- **2026-08-30** (หลัง Sprint F งานที่ 1 + 2a + การแก้ช่องโหว่ CVE-2018-1058) — แก้ 5 จุด:
  1. ข้อ **1a** — ข้าม first triage ไป `opus-worker` เมื่อรู้ว่าต้องสร้างไฟล์ใหม่ (เสีย round trip ซ้ำ
     ด้วยเหตุผลเดิมมาตั้งแต่ Sprint A: `sonnet-worker` ไม่มี Write tool)
  2. ข้อ **4a** — ห้ามให้ `haiku-worker` เขียน prose ที่มีข้อเท็จจริง (แต่งตัวเลขที่ไม่ได้วัดจริง)
  3. **Code Review Flow ข้อ 5** — reviewer ห้ามเชื่อรายงาน worker โดยไม่ verify
  4. **Security Review Flow** — ห้าม apply fix ที่ reviewer เสนอโดยไม่พิสูจน์เอง (เจอ false fix
     3 ครั้งในงานเดียว ทุกอันดูถูกต้องจนกว่าจะทดลองจริง)
  5. **หัวข้อ "การเขียนพรอมต์ dispatch"** — agent ชนเพดาน turn เพราะต้องไปสำรวจเองซ้ำสิ่งที่ owner รู้แล้ว
- **2026-09-02/03** (หลัง Sprint F งานที่ 3 + 4 + การแก้ CombatEndpoints) — แก้ 2 จุด:
  1. **หัวข้อ "ห้ามเขียน 'ยืนยันแล้ว' ก่อนตรวจจริง"** — ขยายข้อ 4a ให้ครอบทุกระดับ ไม่ใช่แค่ haiku
     (เจอ opus-worker sub-worker เขียน "ยืนยันแล้ว" ลง migration comment ก่อน apply migration จริง)
  2. **หัวข้อ "บทเรียนทางเทคนิคเฉพาะโปรเจกต์นี้"** — บันทึกกฎถาวรว่าตารางใหม่ต้องเปิด RLS เอง
     ทุกครั้ง (พบว่า generic RLS-loop เดิมรันครั้งเดียวตอน migration แรก ไม่ใช่ trigger ถาวร
     ตารางใหม่ fail open ถ้าลืม)

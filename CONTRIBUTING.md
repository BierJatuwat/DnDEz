# การร่วมพัฒนา DnDEz

อ่าน [`README.md`](./README.md) ก่อนเพื่อติดตั้งเครื่อง แล้วอ่าน [`docs/PROJECT-CONTEXT.md`](./docs/PROJECT-CONTEXT.md) §2
(การตัดสินใจที่ล็อกแล้ว — **ห้ามเปลี่ยนโดยไม่คุยกันก่อน**) และ [`docs/BACKLOG.md`](./docs/BACKLOG.md) (งานค้าง)

## Branch

```
main        ← ของที่นิ่งแล้ว — เจ้าของโปรเจกต์ merge จาก dev เท่านั้น
 └─ dev     ← รวมงานของทุกคน — รับ PR จาก feature/* เท่านั้น
     └─ feature/<ชื่องาน>   ← ทุกคนทำงานที่นี่
```

- **สร้าง branch ได้แค่ `feature/*`** และแตกจาก `dev` เสมอ — ใช้ชื่อนี้กับทุกงาน รวมแก้บั๊ก
  (เช่น `feature/monster-import`, `feature/fix-grid-offset`)
- **PR เข้า `dev` ได้เฉพาะจาก `feature/*`** · **PR เข้า `main` ได้เฉพาะจาก `dev`** (เจ้าของโปรเจกต์ทำ เมื่อ `dev` นิ่ง)
- ห้าม push ตรงเข้า `main` หรือ `dev`
- CI มี check `branch-flow` ที่ล้มทันทีถ้า PR ผิดทิศ — **ห้าม merge PR ที่ CI แดง** · repo นี้ยังอยู่บนแผนฟรีของ GitHub
  ระบบจึงไม่ได้ล็อกปุ่ม merge ให้ กฎข้างบนเป็นข้อตกลงที่ทุกคนต้องรักษาเอง

## ขั้นตอนทำงาน

1. ดึง `dev` ล่าสุดแล้วแตก branch: `git switch dev && git pull && git switch -c feature/<ชื่องาน>`
2. เขียนโค้ด + เทสต์ (ดูหัวข้อเทสต์ด้านล่าง) แล้วรันเทสต์ทุกชุดในเครื่องให้ผ่าน
3. ก่อนเปิด PR ดึง `dev` ล่าสุดมารวม (`git merge dev`) แล้วรันเทสต์อีกรอบ
4. เปิด Pull Request **เข้า `dev`** — CI (`.github/workflows/ci.yml`) ต้องเขียวทั้ง `branch-flow`, `backend`, `frontend`
5. ถ้า PR แตะ security surface (ดู checklist ท้ายไฟล์) ให้ระบุใน PR ว่าแตะตรงไหน ผู้รีวิวจะดูละเอียดขึ้น
6. งานใน `docs/BACKLOG.md` ที่ทำเสร็จ ให้ลบออกใน PR เดียวกัน · งานค้างใหม่ที่เจอระหว่างทาง ให้เพิ่มเข้าไป
7. merge แล้วลบ branch `feature/*` นั้นทิ้ง

### ข้อความ commit

รูปแบบ `ประเภท(ขอบเขต): คำอธิบาย` — คำอธิบายเป็นภาษาไทยได้ (ทั้ง repo เขียนไทย)

- ประเภท: `feat` · `fix` · `refactor` · `docs` · `test`
- ขอบเขต (ไม่บังคับ): `api` · `frontend` · `db` · `rules`
- บรรทัดแรกบอกว่าทำอะไร ส่วนเนื้อความบอก **ทำไม** และผลเทสต์ (เช่น `backend 894/894`)

ตัวอย่างจากประวัติจริง: `fix(db): Sprint F งานที่ 3 (3/3) — ปิดช่องโหว่ privilege escalation บน campaign_members`

## โครงสร้างและแบบแผนของโค้ด

### Backend (`src/`)

- **endpoint** อยู่ใน `src/DnDEz.Api/Endpoints/<ชื่อ>Endpoints.cs` เป็น static class ที่มี `Map<ชื่อ>Endpoints()`
  แล้วลงทะเบียนใน `Program.cs`
- **route ที่แตะข้อมูลแคมเปญต้องมี `{campaignId}` เสมอ** — middleware อ่าน campaign จาก route ไปตั้ง RLS context
  ถ้าไม่มี policy จะกรองทุกแถวทิ้ง **แบบเงียบ ๆ** (ไม่ error แต่ไม่เห็นข้อมูล)
- **สิทธิ์**: ใช้ helper ใน `Auth/CampaignAuthorization.cs` (`RequireCampaignDmAsync`, `RequireCharacterOwnerAsync`,
  `RequireCharacterActorAsync`) อย่าเขียนเช็คเอง
- **IDOR**: id ทุกตัวที่มาจาก client (รวมที่ AI ส่งมาเป็น tool input) ต้อง query คู่กับ `CampaignId` ของแคมเปญนี้เสมอ
- **logic ที่ใช้ทั้งจาก endpoint และผู้ช่วย DM** แยกเป็นเมธอดที่คืน `OpResult<T>` (`Endpoints/OpResult.cs`)
  ให้ทั้งสองทางเรียกตัวเดียวกัน — ห้ามเขียนซ้ำ
- **rules engine** (`src/DnDEz.Rules`) ห้าม reference EF/Infrastructure/Api และไม่เขียน DB เอง — คืน record ล้วน
- **ห้าม log** API key, เนื้อหาที่ผู้ใช้พิมพ์ หรือ response body ของ Anthropic (อาจมีความลับของ DM)

### ฐานข้อมูล / migration

```bash
dotnet ef migrations add <ชื่อ> --project src/DnDEz.Infrastructure
```

- **ตารางใหม่ทุกตัวที่ผูกแคมเปญ ต้องเขียน `ENABLE ROW LEVEL SECURITY` + `FORCE ROW LEVEL SECURITY` + `CREATE POLICY`
  เองในไฟล์ migration นั้น** และเพิ่มเทสต์ยืนยัน `relrowsecurity`/`relforcerowsecurity` (ตัวอย่าง
  `tests/DnDEz.Api.Tests/EncounterRlsTests.cs`) — ไม่มีอะไรสืบทอดจาก migration เดิม ลืมแล้วทุกคนอ่าน/เขียนได้
- ตรวจไฟล์ที่ generate ทุกครั้ง — เคยเจอ scaffolder ใส่ `AddColumn<uint>("xmin")` ผิด ๆ ต้องลบออกเอง
  (รายละเอียดใน `docs/sprint-g-prompts.md`)
- **ห้ามแก้ migration ที่ push ไปแล้ว** — เพิ่ม migration ใหม่แทน
- คำสั่ง SQL/RLS ที่ semantics ไม่ตรงสัญชาตญาณ (`REVOKE`, `search_path`, policy หลายตัว) ต้องลองกับ DB จริงก่อน —
  ดูบทเรียน "false fix" ใน `CLAUDE.md`

### ผู้ช่วย DM (`src/DnDEz.Api/Assistant/`)

ทุก tool ต้องประกาศ `ToolGate`:

| Gate | ใช้กับ |
|---|---|
| `Immediate` | อ่านอย่างเดียว / เขียนข้อมูลส่วนตัวของ DM |
| `Dial` | เปลี่ยนสถานะเกมแบบมีโครงสร้าง — ทำตาม autonomy dial ของหมวดนั้น |
| `AlwaysConfirmText` | **AI แต่งข้อความเองแล้วส่งถึงผู้เล่น** — DM ต้องเห็นและกดยืนยันทุกครั้ง แม้ dial เป็น Autonomous |

ผู้ช่วย DM เห็นความลับของ DM ได้ ข้อความที่ส่งถึงผู้เล่นจึงต้องผ่านตา DM เสมอ — กฎข้อนี้ dial ข้ามไม่ได้

### Frontend (`frontend/`)

- **Next.js 16 ต่างจากเวอร์ชันที่คุ้นเคย** — อ่าน `frontend/AGENTS.md` และ `node_modules/next/dist/docs/` ก่อนแก้
- ตรรกะล้วนแยกไว้ที่ `src/lib/<ชื่อ>-view.ts` พร้อม `*.test.ts` (vitest) · ดึงข้อมูลผ่าน hook `src/lib/use-<ชื่อ>.ts`
  (TanStack Query) · component ใน `src/components/`
- เรียก API ด้วย `apiFetch` (JSON) — ยกเว้นอัปโหลดไฟล์ (multipart) ที่ต้องใช้ `fetch` ตรง (ดู `use-scene-setup.ts`)
- ข้อความบนหน้าจอเป็นภาษาไทย และเขียนให้คนที่ไม่รู้กฎ D&D เข้าใจได้ (ไม่ให้กรอกค่าที่ตัวเองไม่รู้)

### doc comment

- เขียนภาษาไทย อธิบาย **ทำไม** (ข้อจำกัด, เหตุที่เคยพัง) ไม่ใช่ทำอะไร
- ตัวเลข/ผลการวัดในคอมเมนต์ต้องมาจากการรันจริง — **ห้ามเขียน "ยืนยันแล้ว"/"ตรวจแล้ว" ก่อนรันคำสั่งที่พิสูจน์จริง**
  คอมเมนต์ที่ผิดอันตรายกว่าโค้ดที่ผิด เพราะไม่มีเทสต์จับ แล้วคนรุ่นหลังจะเชื่อเป็นข้อเท็จจริง

## เทสต์

- **API test ต่อ Postgres/Redis/MinIO จริงเสมอ ห้าม mock** — ที่ mock ได้มีที่เดียวคือ Anthropic
  (`tests/DnDEz.Api.Tests/Fakes/FakeAnthropicClient.cs`) เพราะเป็น API ภายนอกที่เสียเงินจริง
- เทสต์แต่ละคลาสสร้างผู้ใช้/แคมเปญของตัวเองด้วย id สุ่ม และลบทิ้งใน `DisposeAsync` — อย่าพึ่งข้อมูลที่มีอยู่ใน DB
- งานที่มี business logic เขียนเทสต์ครอบ edge case ไม่ใช่แค่ happy path
- **แก้ช่องโหว่ต้องมีเทสต์ที่ล้มก่อนแก้** (พิสูจน์ว่าช่องโหว่มีจริง) แล้วค่อยแก้ให้ผ่าน
- กลไกกันความปลอดภัยที่สำคัญ ลองปิดชั่วคราวแล้วดูว่าเทสต์ล้มจริงไหม ก่อนเชื่อว่าเทสต์คุมอยู่

## Checklist ความปลอดภัย

ถ้า PR แตะข้อใดข้อหนึ่ง ให้เขียนบอกใน PR:

- [ ] auth / สิทธิ์ / session
- [ ] รับ input จากภายนอก (endpoint ใหม่, form, อัปโหลดไฟล์ — ตรวจชนิดไฟล์จาก magic bytes ไม่ใช่ Content-Type)
- [ ] query ฐานข้อมูล / ตารางใหม่ (RLS ครบไหม)
- [ ] ข้อมูลที่ไหลเข้า prompt ของ AI — **ความลับของ DM มีทางไปโผล่ในข้อความที่ broadcast ถึงผู้เล่นไหม**
  (รั่วแบบนี้มาแล้ว 4 ครั้ง) · ข้อมูลที่ยังไม่ผ่าน DM review (`ReviewStatus.Pending`) ห้ามเข้า prompt
- [ ] secrets / API key / config
- [ ] dependency ใหม่

## ห้ามทำ

- commit `.env.local`, API key จริง หรือไฟล์ที่มี secret
- push ตรงเข้า `main` หรือ `dev` — ผ่าน PR เสมอ · merge PR ที่ CI ยังแดง
- เปลี่ยนการตัดสินใจใน `PROJECT-CONTEXT.md` §2 โดยไม่คุยกันก่อน
- แยกผู้ช่วย DM กลับเป็นหลายช่องแชท (ตัดสินใจแล้วว่าต้องมีช่องเดียว)

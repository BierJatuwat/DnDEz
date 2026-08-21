# DnDEz — DB schema

> โครงสร้างฐานข้อมูลรอบแรก (2026-08-21) · 30 ตาราง · ตรวจสอบแล้วว่า migrate ขึ้น PostgreSQL 16 + pgvector ได้จริง
> ที่มาของ model: [`dm-side-design.md`](./dm-side-design.md) §3 + [`player-side-design.md`](./player-side-design.md) §23

## แนวทางที่ใช้

- **EF Core 8 code-first** — entity อยู่ใน `src/DnDEz.Domain/Entities/` (แบ่งไฟล์ตามโดเมน ไม่ใช่ไฟล์ละคลาส)
  migration อยู่ใน `src/DnDEz.Infrastructure/Persistence/Migrations/`
- **snake_case ทั้ง DB** (ผ่าน `EFCore.NamingConventions`) — อ่าน schema ตรง ๆ ได้โดยไม่ต้องแปลงชื่อในหัว
- **enum เก็บเป็น text ไม่ใช่ int** + มี `CHECK` constraint กำกับทุกตัว — เปิด DB ดูแล้วรู้เรื่องทันที
  และค่าขยะเล็ดลอดเข้าไม่ได้
- **jsonb** สำหรับของที่ schema เปลี่ยนบ่อยจริง ๆ (character sheet, world state, tool calls, grid/tokens/fog)
  ส่วน list ธรรมดาใช้ Postgres array (`text[]`, `uuid[]`) ไม่ยัดลง jsonb
- ตอนนี้ jsonb ยัง map เป็น `string` ในโค้ด — เปลี่ยนเป็น POCO ทีหลังได้โดยไม่ต้อง migrate

## กฎที่ล็อกไว้ ถูกบังคับที่ชั้น DB แล้ว

ไม่ได้พึ่งโค้ดแอปอย่างเดียว — ถ้าโค้ดมีบั๊ก DB จะปฏิเสธให้เอง (ทดสอบจริงทุกข้อแล้ว)

| กฎ | บังคับด้วย |
|---|---|
| เนื้อหาจาก source import ห้ามหลุดเข้าคลัง preset กลาง (player §19 ข้อ 3) | `ck_presets_global_must_be_srd_only` — `scope='Global'` ต้อง `srd_only=true` |
| preset scope กับ campaign_id ต้องสอดคล้องกัน | `ck_presets_scope_campaign` |
| ทอยเต๋าต้อง replay ตรวจย้อนหลังได้ | `ck_dice_rolls_provenance` — `SystemRoll` ต้องมี `seed`, `DmEntered` ต้องรู้ว่าใครกรอก |
| death save ตามกติกา SRD | `ck_death_saves_range` — 0–3 เท่านั้น |
| reaction timeout อยู่ในช่วงที่สมเหตุสมผล | `ck_campaigns_reaction_timeout` — 5–300 วินาที |
| มี active scene ได้ทีละอันต่อ session | filtered unique index `ix_scenes_one_active_per_session` |
| resource ไม่ติดลบ / ไม่เกิน max | `ck_character_resources_range` |

## Row-Level Security

แอป **ต้องต่อด้วย role `dndez_app`** (ไม่ใช่ owner เพราะ owner bypass RLS) แล้วตั้งค่าทุก request:

```sql
SET LOCAL app.user_id     = '<user guid>';
SET LOCAL app.campaign_id = '<campaign guid>';
```

ไม่ตั้ง = **มองไม่เห็นข้อมูลอะไรเลย** (fail closed ไม่ใช่ fail open)

### ⚠️ บทเรียนตอนทดสอบ — policy ต้องเช็ค "เป็นสมาชิกจริงไหม" ไม่ใช่แค่ค่าที่ตั้งมา

รอบแรกเขียน policy เป็น `campaign_id = app_current_campaign()` เฉย ๆ ซึ่ง**รั่ว** —
พอทดสอบจริงพบว่า DM ของแคมเปญ A แค่ตั้ง `app.campaign_id` เป็นของแคมเปญ B ก็อ่านข้อมูล B ได้ทันที
เพราะ policy เชื่อค่าที่แอปส่งมาโดยไม่ตรวจสอบ

แก้โดยเพิ่ม `app_is_member()` (SECURITY DEFINER) เข้าไปในทุก policy — ตอนนี้ต่อให้แอปตั้งค่าผิด
หรือถูกเจาะจนตั้งค่าเองได้ ก็ยังอ่านข้ามแคมเปญไม่ได้

**หลักที่ได้จากเรื่องนี้**: RLS ที่เชื่อค่าจาก session setting เพียงอย่างเดียว = กันแค่บั๊ก "ลืมใส่ WHERE"
ไม่ได้กัน trust boundary จริง ต้องตรวจสิทธิ์กับข้อมูลใน DB เสมอ

### ระดับการเข้าถึง

| กลุ่มตาราง | policy |
|---|---|
| ตารางที่มี `campaign_id` | เห็นเฉพาะแคมเปญที่ตั้งไว้ **และต้องเป็นสมาชิกจริง** |
| ลูกของ session / character / rest_phase | ไล่ผ่าน FK ไปถึง campaign แล้วใช้กฎเดียวกัน |
| `source_documents`, `raw_chunks` | **DM เท่านั้น** (`app_is_dm()`) — เนื้อหามีลิขสิทธิ์ของ DM คนนั้น |
| `api_keys`, `storage_quotas` | เจ้าของแถวเท่านั้น |
| `character_presets` | `Global` อ่านได้ทุกคน · `Campaign` เฉพาะสมาชิกแคมเปญนั้น |
| `rules_corpus_entries` | ไม่มี RLS — SRD 5.2 เป็น CC-BY อ่านได้ทุกคน |

## ตารางทั้งหมด (30)

**บัญชี** — `users`, `api_keys`, `storage_quotas`, `campaign_members`
**แคมเปญ** — `campaigns`, `autonomy_settings`, `story_beats`, `characters`, `character_resources`,
`death_save_states`, `character_reviews`
**เซสชัน** — `sessions`, `messages`, `private_messages`, `scenes`, `proposals`, `rest_phases`,
`player_rest_actions`, `recap_snapshots`, `consult_messages`, `dice_rolls`
**Source import** — `source_documents`, `raw_chunks`, `monster_stat_blocks`, `npc_profiles`,
`map_assets`, `extracted_character_sheets`
**คลัง/กฎ** — `character_presets`, `rules_corpus_entries`

### ตารางที่เพิ่มจากที่ระบุไว้ในเอกสารออกแบบ

- **`dice_rolls`** — ไม่มีใน data model เดิม แต่ข้อบังคับใน PROJECT-CONTEXT §3 บอกว่า RNG ต้องอยู่ฝั่ง
  server + เก็บ seed ไว้ replay ตรวจข้อพิพาท ถ้าไม่มีตารางนี้ก็ทำตามกฎนั้นไม่ได้
- **`rules_corpus_entries`** — SRD 5.2 ที่ฝังมากับระบบ ใช้ทำ condition tooltip โดยไม่เรียก AI (player §13)
- **`extracted_character_sheets`** — staging ของ companion mode ก่อน DM approve (player §20)

## ยังไม่ได้ทำ / ต้องตัดสินใจ

- **มิติของ `raw_chunks.embedding` ตอนนี้ตั้งไว้ 1536 ชั่วคราว** — ⚠️ Anthropic ไม่มี embeddings API
  ต้องเลือก provider ก่อน (คนละเรื่องกับ image generation ที่ค้างอยู่ใน PROJECT-CONTEXT §4)
  แล้วค่อยแก้มิติให้ตรง
- ยังไม่ได้สร้าง **HNSW/IVFFlat index** บน `embedding` — รอจนรู้ provider และปริมาณข้อมูลจริงก่อน
  (สร้าง index ผิดชนิดตอนข้อมูลน้อยเสียเวลาเปล่า)
- ยังไม่มี seed data ของ SRD 5.2 ใน `rules_corpus_entries`
- ยังไม่ได้ทำตาราง Hangfire (สร้างเองอัตโนมัติตอนรันครั้งแรก)

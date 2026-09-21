# นำเข้า SRD 5.2.1

สร้างไฟล์ข้อมูลที่ migration `ImportSrd521` ฝังไว้ (`src/DnDEz.Infrastructure/Seed/Srd/*.json`) จาก `docs/SRD_CC_v5.2.1.pdf`

```bash
python tools/srd-import/build_srd_data.py
```

**ไฟล์ PDF ไม่ได้อยู่ใน repo** — ดาวน์โหลด `SRD_CC_v5.2.1.pdf` จาก https://www.dndbeyond.com/srd แล้ววางที่ `docs/SRD_CC_v5.2.1.pdf` ก่อนรัน

ต้องมี `python` 3.10+ และ `pdftotext` ใน PATH · สคริปต์ **พังทันที** ถ้าผลไม่ตรงตัวเลขอ้างอิงในต้นฉบับ
(ดัชนี stat block, รายชื่อเวทของแต่ละคลาส, รายการ condition, HP เทียบสูตรเต๋า)

| ไฟล์ | หน้าที่ |
|---|---|
| `srd_text.py` | ดึงข้อความ (`pdftotext -enc UTF-8 -raw`), ตัดหัวกระดาษ, ต่อบรรทัด/คำที่ถูกตัด, แบ่งย่อหน้า |
| `parse_monsters.py` | stat block 330 ตัว |
| `parse_spells.py` | เวท 339 ตัว |
| `parse_glossary.py` | บท Rules Glossary — **เชื่อถือได้เฉพาะหัวข้อที่มีป้าย `[Condition]` ฯลฯ** |
| `build_srd_data.py` | รวม + ตรวจ + เขียนไฟล์ JSON |

**ห้ามแก้ไฟล์ JSON ด้วยมือ** และห้ามแก้ migration ที่ apply แล้ว — ถ้าต้องแก้ข้อมูล ให้แก้ parser → สร้างไฟล์เวอร์ชันใหม่ → เพิ่ม migration ที่ upsert

ต้องแสดงข้อความ attribution ของ CC-BY-4.0 ต่อผู้ใช้ (`GET /srd/attribution`)

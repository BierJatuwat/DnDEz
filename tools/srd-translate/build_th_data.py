# -*- coding: utf-8 -*-
"""สร้างไฟล์คำแปลไทยของ SRD 5.2.1 ที่ฝังเข้า migration (src/DnDEz.Infrastructure/Seed/Srd/*-th.json)

รันจากรากของ repo:   python tools/srd-translate/build_th_data.py
ต้องรันหลัง `tools/srd-import/build_srd_data.py` เสมอ (อ่านไฟล์ภาษาอังกฤษที่สคริปต์นั้นสร้างไว้เป็นฐาน)

**กฎเดียวกับ `srd-import`**: ถ้าคำแปลไม่ครบตามที่กำหนด (ชื่อมอนสเตอร์/เวทต้องครบ 100%, กฎ/สถานะต้องครบ
100%) หรือ key ที่แปลไว้ไม่ตรงกับ key ในไฟล์อังกฤษ ให้ **พังทันที** ไม่ใช่เขียนไฟล์ที่ข้อมูลขาดเงียบ ๆ

**ขอบเขตของรอบนี้** (2026-09-22 — ตามที่ผู้ใช้เลือก "ทำโครงสร้างให้ครบ + แปลชุดที่ใช้จริงก่อน"):
 - ขนาด/ชนิด/แนวคิด/สำนักเวท/คลาส แปลครบทุกค่า (คำศัพท์ปิด ใช้ได้กับทุกแถวแทบไม่มีต้นทุนเพิ่ม)
 - สถานะ 15 + กฎมีป้ายกำกับ 26 แปลเนื้อหาเต็มครบ 41/41
 - รายละเอียดเต็ม (traits/actions/คำอธิบายเวท) แปลแล้วเฉพาะ 8 มอนสเตอร์ + 4 เวทที่ใช้งานจริงในระบบตอนนี้
   ตัวที่เหลือ API จะคืนค่าอังกฤษพร้อมธง `contentTranslated: false` ให้ frontend รู้ว่ายังไม่มีคำแปล

**2026-09-22 (แก้ตามคำขอผู้ใช้): ชื่อมอนสเตอร์/ชื่อเวท/ชื่อ condition-rule ไม่แปล**
ผู้ใช้ระบุชัดว่าชื่อไอเทม ชื่อมอนสเตอร์ และ "ชื่อเฉพาะ" (รวมชื่อเวทกับชื่อ condition/rule ตามที่ยืนยัน
ผ่าน AskUserQuestion) ให้คงเป็นอังกฤษเสมอ แปลเฉพาะ "บริบทโดยรอบ" — ดังนั้นไฟล์นี้ใส่ชื่ออังกฤษลงในช่อง
`name`/`title` ของ payload ไทยตรง ๆ (ไม่ผ่าน `monster_names_th.py`/`spell_names_th.py` และไม่ใช้ครึ่ง
`title_th` ของ `rules_th.py` อีกต่อไป) ไฟล์ทั้งสามนั้นยังเก็บไว้เป็นข้อมูลอ้างอิง **ห้ามนำกลับมาใช้แปลชื่อ**
เว้นแต่ผู้ใช้เปลี่ยนใจภายหลัง — ส่วน `text_th`/เนื้อหาเต็มของ `rules_th.py` ยังใช้ตามเดิม (เป็นบริบท ไม่ใช่ชื่อ)
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
SEED_DIR = REPO / "src" / "DnDEz.Infrastructure" / "Seed" / "Srd"
VERSION = "5.2.1"

sys.path.insert(0, str(HERE))
from th_glossary import ALIGNMENT_TH, CLASS_TH, SCHOOL_TH, SIZE_TH, TYPE_TAG_TH, TYPE_TH  # noqa: E402
from rules_th import RULES_TH  # noqa: E402
from starter_full_th import MONSTER_FULL_TH, SPELL_FULL_TH  # noqa: E402


def main() -> None:
    monsters_en = json.loads((SEED_DIR / f"srd-{VERSION}-monsters.json").read_text(encoding="utf-8"))
    spells_en = json.loads((SEED_DIR / f"srd-{VERSION}-spells.json").read_text(encoding="utf-8"))
    rules_en = json.loads((SEED_DIR / f"srd-{VERSION}-rules.json").read_text(encoding="utf-8"))

    # ── มอนสเตอร์ ──────────────────────────────────────────────────────────
    en_monster_keys = {m["key"] for m in monsters_en}
    unknown_full = set(MONSTER_FULL_TH) - en_monster_keys
    assert not unknown_full, "MONSTER_FULL_TH มี key ที่ไม่มีในต้นฉบับ: %s" % sorted(unknown_full)

    monsters_th = []
    for m in monsters_en:
        assert m["size"] in SIZE_TH, "ไม่มีคำแปลขนาด: %r (มอนสเตอร์ %s)" % (m["size"], m["key"])
        assert m["type"] in TYPE_TH, "ไม่มีคำแปลชนิด: %r (มอนสเตอร์ %s)" % (m["type"], m["key"])
        assert m["alignment"] in ALIGNMENT_TH, "ไม่มีคำแปลแนวคิด: %r (มอนสเตอร์ %s)" % (m["alignment"], m["key"])
        if m["typeTag"]:
            assert m["typeTag"] in TYPE_TAG_TH, "ไม่มีคำแปลป้ายกำกับ: %r (มอนสเตอร์ %s)" % (m["typeTag"], m["key"])

        row = {
            "key": m["key"],
            # ชื่อมอนสเตอร์เป็น "ชื่อเฉพาะ" — ตามคำขอผู้ใช้ (2026-09-22) ไม่แปล คงเป็นอังกฤษเสมอ
            "name": m["name"],
            "size": SIZE_TH[m["size"]],
            "type": TYPE_TH[m["type"]],
            "typeTag": TYPE_TAG_TH.get(m["typeTag"]) if m["typeTag"] else None,
            "alignment": ALIGNMENT_TH[m["alignment"]],
        }
        full = MONSTER_FULL_TH.get(m["key"])
        if full is not None:
            # ต้องมีครบทุกส่วนที่ต้นฉบับมี (ห้ามแปลครึ่งเดียวแล้วปล่อยส่วนที่เหลือว่างเปล่าโดยไม่ตั้งใจ)
            for section in ("traits", "actions", "bonusActions", "reactions", "legendaryActions"):
                assert len(full.get(section, [])) == len(m[section]), (
                    "%s ของ %s: อังกฤษมี %d รายการ แต่คำแปลมี %d รายการ"
                    % (section, m["key"], len(m[section]), len(full.get(section, [])))
                )
            row["data"] = full
        monsters_th.append(row)

    # ── เวท ────────────────────────────────────────────────────────────────
    en_spell_keys = {s["key"] for s in spells_en}
    unknown_full_spell = set(SPELL_FULL_TH) - en_spell_keys
    assert not unknown_full_spell, "SPELL_FULL_TH มี key ที่ไม่มีในต้นฉบับ: %s" % sorted(unknown_full_spell)

    spells_th = []
    for s in spells_en:
        assert s["school"] in SCHOOL_TH, "ไม่มีคำแปลสำนักเวท: %r (เวท %s)" % (s["school"], s["key"])
        for c in s["classes"]:
            assert c in CLASS_TH, "ไม่มีคำแปลคลาส: %r (เวท %s)" % (c, s["key"])

        row = {
            "key": s["key"],
            # ชื่อเวทเป็น "ชื่อเฉพาะ" — ตามคำขอผู้ใช้ (2026-09-22) ไม่แปล คงเป็นอังกฤษเสมอ
            "name": s["name"],
            "school": SCHOOL_TH[s["school"]],
            "classes": [CLASS_TH[c] for c in s["classes"]],
        }
        full = SPELL_FULL_TH.get(s["key"])
        if full is not None:
            description_th, higher_level_th = full
            row["description"] = description_th
            row["higherLevel"] = higher_level_th
        spells_th.append(row)

    # ── กฎ/สถานะ — บังคับครบ 100% ────────────────────────────────────────────
    en_rule_keys = {r["key"] for r in rules_en}
    assert en_rule_keys == set(RULES_TH), (
        "คำแปลกฎ/สถานะไม่ครบ — ขาด: %s | เกิน: %s"
        % (sorted(en_rule_keys - set(RULES_TH)), sorted(set(RULES_TH) - en_rule_keys))
    )
    rules_th = [
        # title = อังกฤษตรง ๆ ("ชื่อเฉพาะ" ตามคำขอผู้ใช้ 2026-09-22 — ไม่แปล) · text = บริบทเต็ม แปลตามเดิม
        {"key": r["key"], "tag": r["tag"], "title": r["title"], "text": RULES_TH[r["key"]][1]}
        for r in rules_en
    ]

    SEED_DIR.mkdir(parents=True, exist_ok=True)

    def dump(name: str, payload) -> None:
        (SEED_DIR / name).write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    dump(f"srd-{VERSION}-monsters-th.json", monsters_th)
    dump(f"srd-{VERSION}-spells-th.json", spells_th)
    dump(f"srd-{VERSION}-rules-th.json", rules_th)

    full_monster_count = sum(1 for m in monsters_th if "data" in m)
    full_spell_count = sum(1 for s in spells_th if "description" in s)
    print(
        "monsters(rows)=%d/%d monsters(full)=%d spells(rows)=%d/%d spells(full)=%d rules(full)=%d/%d"
        % (
            len(monsters_th), len(monsters_en), full_monster_count,
            len(spells_th), len(spells_en), full_spell_count,
            len(rules_th), len(rules_en),
        )
    )


if __name__ == "__main__":
    main()

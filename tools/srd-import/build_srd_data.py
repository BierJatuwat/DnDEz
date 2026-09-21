"""สร้างไฟล์ข้อมูล SRD 5.2.1 ที่ฝังเข้า migration (src/DnDEz.Infrastructure/Seed/Srd/*.json)

รันจากรากของ repo:   python tools/srd-import/build_srd_data.py
ต้องมี: python 3.10+ และ `pdftotext` (poppler/xpdf) ใน PATH · ต้นฉบับ: docs/SRD_CC_v5.2.1.pdf

**กฎของสคริปต์นี้**: ถ้าจำนวนที่ได้ไม่ตรงกับตัวเลขอ้างอิงในต้นฉบับ (ดัชนี stat block ท้ายเล่ม, รายชื่อเวทของแต่ละคลาส,
รายการ condition ในบท Rules Glossary) ให้ **พังทันที** ไม่ใช่เขียนไฟล์ที่ข้อมูลหายเงียบ ๆ
"""
import json
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
OUT = REPO / "src" / "DnDEz.Infrastructure" / "Seed" / "Srd"
VERSION = "5.2.1"
ATTRIBUTION = (
    'This work includes material from the System Reference Document 5.2.1 ("SRD 5.2.1") by Wizards of the Coast LLC, '
    "available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0 "
    "International License, available at https://creativecommons.org/licenses/by/4.0/legalcode."
)

sys.path.insert(0, str(HERE))
from srd_text import extract_lines, strip_page_furniture  # noqa: E402


def run(script):
    r = subprocess.run([sys.executable, str(HERE / script)], capture_output=True, check=True, env={**__import__("os").environ, "PYTHONUTF8": "1"})
    return json.loads(r.stdout.decode("utf-8"))


def slug(name):
    return re.sub(r"[^a-z0-9]+", "-", name.lower().replace("’", "").replace("'", "")).strip("-")


def index_of_stat_blocks(lines):
    i = [k for k, l in enumerate(lines) if l.startswith("Index of Stat")][-1]
    names = []
    for l in lines[i:i + 500]:
        m = re.match(r"^(.*?)\.{3,}\s*(\d+)$", l.strip())
        if m:
            names.append(m.group(1).strip())
    return names


def derive_monster(m):
    # เดินปกติ = ตัวเลขแรกของบรรทัด Speed ("30 ft., Fly 60 ft." → 30) · ไม่มีเดิน (เช่นบินอย่างเดียว) = 0
    sm = re.match(r"^(\d+) ft\.", m["speedText"] or "")
    m["speedFeet"] = int(sm.group(1)) if sm else 0
    # ระยะเอื้อม = ค่า "reach N ft." สูงสุดในท่าทั้งหมด · ไม่มีท่าระยะประชิดเลย = 5 (SRD 5.2 "Reach": ทุกสิ่งมีชีวิตเอื้อม 5 ฟุต)
    reaches = [int(x) for sec in ("actions", "bonusActions", "reactions", "traits", "legendaryActions")
               for e in m[sec] for x in re.findall(r"reach (\d+) ft\.", e["text"])]
    m["reachFeet"] = max(reaches) if reaches else 5
    m["key"] = slug(m["name"])
    return m


def main():
    lines = strip_page_furniture(extract_lines())

    # ── มอนสเตอร์ ──
    mon = run("parse_monsters.py")
    assert not mon["errors"], mon["errors"]
    monsters = [derive_monster(m) for m in mon["monsters"]]
    idx = index_of_stat_blocks(lines)
    assert sorted(m["name"] for m in monsters) == sorted(idx), "รายชื่อมอนสเตอร์ไม่ตรงดัชนีท้ายเล่ม"
    assert len({m["key"] for m in monsters}) == len(monsters), "key ซ้ำ"
    for m in monsters:
        assert len(m["abilities"]) == 6, m["name"]
        # HP ต้องตรงกับสูตรเต๋า (เฉลี่ยปัดลง) และโบนัสเต๋าต้องเท่ากับ จำนวนลูกเต๋า × CON modifier — จับการอ่านตัวเลขผิดได้จริง
        hd = re.match(r"^(\d+)d(\d+)(?: ([+\-−]) (\d+))?$", m["hitDice"])
        assert hd, "%s: hitDice แปลก %r" % (m["name"], m["hitDice"])
        n, faces = int(hd.group(1)), int(hd.group(2))
        bonus = int(hd.group(4) or 0) * (-1 if hd.group(3) in ("-", "−") else 1)
        assert bonus == n * m["abilities"]["con"]["mod"], "%s: โบนัสเต๋า %d != %d×CON" % (m["name"], bonus, n)
        assert m["hitPoints"] == int(n * (faces + 1) / 2 + bonus), "%s: HP ไม่ตรงสูตรเต๋า" % m["name"]

    # ── เวท ──
    sp = run("parse_spells.py")
    assert not sp["errors"], sp["errors"]
    spells = sp["spells"]
    for s in spells:
        s["key"] = slug(s["name"])
    assert len({s["key"] for s in spells}) == len(spells), "key เวทซ้ำ"
    # ตรวจกับรายชื่อเวทในบทคลาส: ทุกชื่อในลิสต์ต้องมีในเวทที่แยกได้ ("Minor"/"Programmed" คือชื่อ 2 บรรทัดที่ถูกตัด)
    sch = "Abjuration|Conjuration|Divination|Enchantment|Evocation|Illusion|Necromancy|Transmutation"
    end = next(i for i, l in enumerate(lines) if l.strip() == "Spell Descriptions" and i > 3000)
    listed = set()
    for l in lines[:end]:
        m = re.match(r"^([A-Z][A-Za-z'’ /\-]+?) (?:%s)\b.*$" % sch, l.strip())
        if m and len(m.group(1).split()) <= 5:
            listed.add(m.group(1).strip())
    names = {s["name"] for s in spells}
    missing = {n for n in listed - names if n not in ("Minor", "Programmed")}
    assert not missing, "เวทในลิสต์ของคลาสที่ไม่ได้แยกมา: %s" % sorted(missing)
    assert len(spells) == 339, len(spells)

    # ── กฎที่มีป้ายกำกับ (แม่นยำ) ──
    gl = run("parse_glossary.py")
    tagged = [e for e in gl["entries"] if e["tag"]]
    cond = [e for e in tagged if e["tag"] == "Condition"]
    assert sorted(e["title"] for e in cond) == sorted([
        "Blinded", "Charmed", "Deafened", "Exhaustion", "Frightened", "Grappled", "Incapacitated", "Invisible",
        "Paralyzed", "Petrified", "Poisoned", "Prone", "Restrained", "Stunned", "Unconscious"]), "condition ไม่ครบ 15"
    rules = [{"key": slug(e["title"]), "title": e["title"], "tag": e["tag"], "text": e["text"]} for e in tagged]
    assert len({(r["tag"], r["key"]) for r in rules}) == len(rules)

    OUT.mkdir(parents=True, exist_ok=True)
    def dump(name, payload):
        (OUT / name).write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    dump("srd-%s-monsters.json" % VERSION, monsters)
    dump("srd-%s-spells.json" % VERSION, spells)
    dump("srd-%s-rules.json" % VERSION, rules)
    print("monsters=%d spells=%d rules(tagged)=%d conditions=%d" % (len(monsters), len(spells), len(rules), len(cond)))
    print("attribution:", ATTRIBUTION[:60], "...")


if __name__ == "__main__":
    main()

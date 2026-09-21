"""SRD 5.2.1 → รายการมอนสเตอร์ (JSON) — เฉพาะบท "Monsters" ไม่รวม stat block ของเวทเรียกอัญเชิญ
(Otherworldly Steed, Giant Insect, Draconic Spirit … AC/HP ขึ้นกับระดับช่องเวท จึงเป็นข้อความเวท ไม่ใช่มอนสเตอร์)
"""
import json
import re
import sys

from srd_text import extract_lines, strip_page_furniture, join_wrapped

SIZE_RE = re.compile(r"^(Tiny|Small|Medium|Large|Huge|Gargantuan)\b")
SECTIONS = ("Traits", "Actions", "Bonus Actions", "Reactions", "Legendary Actions")
# " ?" หลังชื่อ + เครื่องหมายของ save เป็นทางเลือก: ต้นฉบับ PDF มีพิมพ์ผิด 2 จุด ("Con22" ของ Adult White Dragon
# และ "Int 6 −2 2" ของ Young White Dragon ที่ตก − หน้า save) — ดูการแก้ใน fix_ability()
ABILITY_ROW = re.compile(r"(Str|Dex|Con|Int|Wis|Cha) ?(\d+) ([+\-−]\d+) ([+\-−]?\d+)")
SIZES = "Tiny|Small|Medium|Large|Huge|Gargantuan"
LABELS = ["Skills", "Gear", "Vulnerabilities", "Resistances", "Immunities", "Senses", "Languages"]
FIELD_START = re.compile(r"^(HP|Speed|MOD SAVE|Str |Skills|Gear|Senses|Languages|Vulnerabilities|Resistances|Immunities)\b")
ENTRY_START = re.compile(r"^([A-Z][A-Za-z'’\- ]+(?: \([^)]*\))?)\. (.*)$")
TITLE_SMALL_WORDS = {"of", "the", "and", "or", "a", "in", "to", "with", "on"}


def num(s: str) -> int:
    return int(s.replace("−", "-"))


def fix_ability(name, a):
    """แปลงค่าความสามารถหนึ่งช่อง พร้อมแก้พิมพ์ผิดของต้นฉบับ *เฉพาะกรณีที่พิสูจน์ได้จากสูตร*

    กฎ D&D: mod = floor((score-10)/2) เสมอ · ถ้า save ที่อ่านได้ไม่มีเครื่องหมายลบทั้งที่ mod ติดลบและ
    ค่าตัวเลขตรงกับ |mod| พอดี (save = mod เมื่อไม่ได้ฝึกเซฟ) แปลว่าเครื่องหมายลบตกหล่นจากการแปลง PDF
    """
    score, mod, save = int(a.group(2)), num(a.group(3)), a.group(4)
    if mod != (score - 10) // 2:
        raise ValueError("%s: mod %d ไม่ตรงสูตรของ score %d" % (name, mod, score))
    fixed = None
    if save[0] in "+-−":
        sv = num(save)
    else:
        sv = int(save)
        if mod < 0 and sv == -mod:
            sv, fixed = mod, "save เครื่องหมายลบตกหล่นในต้นฉบับ อ่านได้ %s แก้เป็น %d" % (save, mod)
    r = {"score": score, "mod": mod, "save": sv}
    if fixed:
        r["sourceFixup"] = fixed
    return r


def find_starts(lines):
    """จุดเริ่ม stat block: บรรทัดขนาด แล้วบรรทัดถัดไปขึ้นต้น "AC <ตัวเลข>" """
    return [i for i in range(1, len(lines) - 1)
            if SIZE_RE.match(lines[i]) and re.match(r"^AC \d+", lines[i + 1])]


def parse_header(size_line):
    # "Small Fey (Goblinoid), Chaotic Neutral" / "Large Celestial, Fey, or Fiend (Your Choice), Neutral"
    head, _, alignment = size_line.rpartition(", ")
    m = re.match(r"^((?:%s)(?: or (?:%s))?) (.*)$" % (SIZES, SIZES), head)
    size, rest = (m.group(1), m.group(2)) if m else ("", head)
    tag = None
    tm = re.match(r"^(.*?) \((.*)\)$", rest)
    if tm:
        rest, tag = tm.group(1), tm.group(2)
    return size, rest, tag, alignment


def looks_like_title(t: str) -> bool:
    # ชื่อท่า: คำหลักขึ้นต้นตัวใหญ่ ยาวไม่เกิน 7 คำ — กันประโยคธรรมดาที่บังเอิญมีจุดกลางบรรทัด
    words = t.split()
    return 0 < len(words) <= 7 and all(
        w[0].isupper() or w[0] == "(" or w[0].isdigit() or w.lower() in TITLE_SMALL_WORDS
        for w in words)


def split_entries(ls):
    entries, cur = [], None
    for l in ls:
        em = ENTRY_START.match(l)
        if em and (cur is None or looks_like_title(em.group(1))):
            cur = {"name": em.group(1), "lines": [em.group(2)]}
            entries.append(cur)
        elif cur is not None:
            cur["lines"].append(l)
        elif l.strip():
            cur = {"name": "", "lines": [l]}
            entries.append(cur)
    return [{"name": e["name"], "text": join_wrapped(e["lines"])} for e in entries]


def parse_block(name, blk):
    size, mtype, tag, alignment = parse_header(blk[0])
    d = {"name": name, "size": size, "type": mtype, "typeTag": tag, "alignment": alignment}

    body = blk[1:]
    cr_idx = next((k for k, l in enumerate(body) if l.startswith("CR ")), None)
    if cr_idx is None:
        raise ValueError("ไม่พบบรรทัด CR")
    head = body[:cr_idx]
    # 3 รูปแบบที่เจอ: "CR 1/4 (XP 50; PB +2)" · "CR 3 (700 XP; PB +2)" ·
    # "CR 10 (XP 5,900, or 7,200 in lair; PB +4)" (ตัวหลัง = มีค่า XP ตอนอยู่ในถ้ำด้วย)
    m = re.match(r"^CR ([\d/]+|None) \((?:XP )?([\d,]+)(?: XP)?(?:, or ([\d,]+) in lair)?; PB (?:\+(\d+)|[^)]*)\)", body[cr_idx])
    if not m:
        raise ValueError("CR line แปลก: %r" % body[cr_idx])
    d["cr"] = m.group(1)
    d["xp"] = int(m.group(2).replace(",", ""))
    d["xpInLair"] = int(m.group(3).replace(",", "")) if m.group(3) else None
    d["proficiencyBonus"] = int(m.group(4)) if m.group(4) else None

    ac_m = re.match(r"^AC (\d+)(?:\s+Initiative ([+\-−]\d+) \((\d+)\))?", head[0])
    d["armorClass"] = int(ac_m.group(1))
    init = ac_m.group(2)
    idx = 1
    if init is None and idx < len(head):
        im = re.match(r"^Initiative ([+\-−]\d+) \((\d+)\)", head[idx])
        if im:
            init = im.group(1)
            idx += 1
    d["initiativeBonus"] = num(init) if init is not None else None

    def take_field(prefix):
        nonlocal idx
        if idx < len(head) and head[idx].startswith(prefix):
            buf = [head[idx]]
            idx += 1
            while idx < len(head) and not FIELD_START.match(head[idx]):
                buf.append(head[idx])
                idx += 1
            return join_wrapped(buf)[len(prefix):].strip()
        return None

    hp = take_field("HP ")
    d["hpText"] = hp
    hm = re.match(r"^(\d+) \(([^)]*)\)", hp or "")
    d["hitPoints"] = int(hm.group(1)) if hm else None
    d["hitDice"] = hm.group(2) if hm else None
    d["speedText"] = take_field("Speed ")

    d["abilities"] = {}
    while idx < len(head) and (head[idx].startswith("MOD SAVE") or ABILITY_ROW.search(head[idx])):
        for a in ABILITY_ROW.finditer(head[idx]):
            d["abilities"][a.group(1).lower()] = fix_ability(name, a)
        idx += 1

    cur, fields = None, {}
    for l in head[idx:]:
        lm = re.match(r"^(" + "|".join(LABELS) + r") (.*)$", l)
        if lm:
            cur = lm.group(1)
            fields[cur] = [lm.group(2)]
        elif cur:
            fields[cur].append(l)
    for k, v in fields.items():
        d[k[0].lower() + k[1:]] = join_wrapped(v)

    sections, cur = {"Traits": []}, "Traits"
    for l in body[cr_idx + 1:]:
        if l in SECTIONS:
            cur = l
            sections.setdefault(cur, [])
            continue
        sections[cur].append(l)
    d["traits"] = split_entries(sections.get("Traits", []))
    d["actions"] = split_entries(sections.get("Actions", []))
    d["bonusActions"] = split_entries(sections.get("Bonus Actions", []))
    d["reactions"] = split_entries(sections.get("Reactions", []))
    d["legendaryActions"] = split_entries(sections.get("Legendary Actions", []))
    return d


def main():
    lines = strip_page_furniture(extract_lines())
    start = next(i for i, l in enumerate(lines) if l.startswith("Monsters A") and i > 1000)
    lines = lines[start:]
    starts = find_starts(lines)
    monsters, errors = [], []
    for n, s in enumerate(starts):
        name = lines[s - 1].strip()
        end = starts[n + 1] - 1 if n + 1 < len(starts) else len(lines)
        blk = lines[s:end]
        # ชื่อกลุ่ม (เช่น "Gold Dragons") อาจติดท้ายบล็อก — ตัดบรรทัดสั้นที่ไม่ลงท้ายประโยคออก
        while blk and not blk[-1].rstrip().endswith((".", ")", "*")) and len(blk[-1].split()) <= 5 \
                and blk[-1] not in SECTIONS:
            blk.pop()
        try:
            monsters.append(parse_block(name, blk))
        except Exception as e:  # noqa: BLE001
            errors.append("%s: %s" % (name, e))
    json.dump({"monsters": monsters, "errors": errors}, sys.stdout, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()

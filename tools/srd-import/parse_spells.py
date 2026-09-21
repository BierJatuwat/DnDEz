"""SRD 5.2.1 → รายการเวท (JSON) จากบท "Spell Descriptions" (ไม่ใช่รายชื่อเวทท้ายบทคลาส)"""
import json
import re
import sys

from srd_text import extract_lines, strip_page_furniture, join_wrapped, paragraphs

HEADER = re.compile(r"^(?:Level (\d) ([A-Z][a-z]+)|([A-Z][a-z]+) Cantrip) \(([^)]*)\)$")
LABELS = ("Casting Time", "Range", "Components", "Duration")
# ต้นฉบับสะกด "Component:" (เอกพจน์) ในเวทที่มีองค์ประกอบเดียว (Guidance, Barkskin, Divine Smite ฯลฯ) — รองรับทั้งสองแบบ
LABEL_RE = re.compile(r"^(Casting Time|Range|Components?|Duration): ?(.*)$")
UPCAST_RE = re.compile(r"^(Using a Higher-Level Spell Slot|Cantrip Upgrade)\. (.*)$")


def find_body(lines):
    # "Spell Descriptions" โผล่ 2 ที่: สารบัญ กับหัวข้อจริง (ตามด้วยข้อความเวท) — เอาที่มีบรรทัดหัวเวทตามมาใกล้ ๆ
    for i, l in enumerate(lines):
        if l.strip() == "Spell Descriptions" and any(HEADER.match(x) for x in lines[i + 1:i + 12]):
            return i
    raise RuntimeError("ไม่พบจุดเริ่มบทเวท")


HEADER_START = re.compile(r"^(?:Level \d [A-Z][a-z]+|[A-Z][a-z]+ Cantrip) \(")


def merge_wrapped_headers(body):
    """หัวเวทที่มีรายชื่อคลาสยาวจะถูกตัดขึ้นบรรทัดใหม่ ("... (Bard, Cleric, Druid, Paladin,\nRanger)") — รวมกลับเป็นบรรทัดเดียว
    ก่อนหา header ไม่งั้นเวทกลุ่มนี้หายเงียบ (เจอตอน cross-check กับรายชื่อเวทท้ายบทคลาส: หายไป 37 ตัว)"""
    out, i = [], 0
    while i < len(body):
        l = body[i]
        if HEADER_START.match(l) and not l.rstrip().endswith(")"):
            j = i + 1
            merged = l
            while j < len(body) and j <= i + 3 and not merged.rstrip().endswith(")"):
                merged += " " + body[j]
                j += 1
            if merged.rstrip().endswith(")"):
                out.append(merged)
                i = j
                continue
        out.append(l)
        i += 1
    return out


def main():
    lines = strip_page_furniture(extract_lines())
    start = find_body(lines)
    end = next(i for i in range(start, len(lines)) if lines[i].strip() == "Rules Glossary")
    body = merge_wrapped_headers(lines[start:end])

    heads = [i for i in range(1, len(body)) if HEADER.match(body[i]) and body[i + 1].startswith("Casting Time:")]
    spells, errors = [], []
    for n, h in enumerate(heads):
        name = body[h - 1].strip()
        stop = heads[n + 1] - 1 if n + 1 < len(heads) else len(body)
        blk = body[h + 1:stop]
        hm = HEADER.match(body[h])
        level = int(hm.group(1)) if hm.group(1) else 0
        school = hm.group(2) or hm.group(3)
        classes = [c.strip() for c in hm.group(4).split(",")]

        fields, cur, k = {}, None, 0
        while k < len(blk):
            lm = LABEL_RE.match(blk[k])
            if lm:
                cur = "Components" if lm.group(1).startswith("Component") else lm.group(1)
                fields[cur] = [lm.group(2)]
            elif cur and len(fields) < 4:
                fields[cur].append(blk[k])
            else:
                break
            k += 1
            if len(fields) == 4 and (k >= len(blk) or not blk[k].startswith(" ")) and cur == "Duration":
                # Duration อาจยาวสองบรรทัดเป็นกรณีพิเศษ ตรวจด้านล่าง
                break
        missing = [x for x in LABELS if x not in fields]
        if missing:
            errors.append("%s: ขาด %s" % (name, missing))
            continue
        desc = blk[k:]
        # ตัดส่วนอัปเกรดเป็นฟิลด์แยก
        main_lines, upcast_lines, in_up = [], [], False
        for l in desc:
            if UPCAST_RE.match(l):
                in_up = True
            (upcast_lines if in_up else main_lines).append(l)
        upcast = join_wrapped(upcast_lines) if upcast_lines else None
        spells.append({
            "name": name, "level": level, "school": school, "classes": classes,
            "castingTime": join_wrapped(fields["Casting Time"]),
            "range": join_wrapped(fields["Range"]),
            "components": join_wrapped(fields["Components"]),
            "duration": join_wrapped(fields["Duration"]),
            "description": paragraphs(main_lines),
            "higherLevel": upcast,
        })
    json.dump({"spells": spells, "errors": errors}, sys.stdout, ensure_ascii=False, indent=1)



if __name__ == "__main__":
    main()

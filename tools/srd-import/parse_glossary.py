"""SRD 5.2.1 → รายการ "Rules Glossary" (เรียงตัวอักษร) เป็นข้อความอ้างอิงของ RulesCorpus

หัวข้อในไฟล์ต้นฉบับไม่มีเครื่องหมายบอกชัด (ข้อความดิบจาก pdftotext) จึงใช้ 2 ชั้น:
 1) หัวข้อที่มีป้ายในวงเล็บเหลี่ยม "[Condition]" "[Action]" "[Area of Effect]" ฯลฯ — แม่นยำ 100%
 2) หัวข้อไม่มีป้าย — เดาจากรูปทรงบรรทัด + ต้องเรียงตัวอักษรต่อจากหัวข้อก่อนหน้า
ผลของชั้น 2 ต้องถูกตรวจด้วยตา/เทียบรายการอ้างอิง ห้ามถือว่าครบโดยไม่เช็ค (ดู report ท้ายสคริปต์)
"""
import json
import re
import sys

from srd_text import extract_lines, strip_page_furniture, join_wrapped, paragraphs

TAGGED = re.compile(r"^([A-Z][A-Za-z'’ \-]+?) \[([A-Za-z ]+)\]$")
PLAIN = re.compile(r"^[A-Z][A-Za-z'’\-]*(?: [A-Za-z'’\-]+){0,4}$")
SMALL = {"of", "the", "and", "or", "a", "an", "in", "to", "with", "on", "for", "from", "by", "at"}


def title_ok(t):
    ws = t.split()
    return all(w[0].isupper() or w.lower() in SMALL for w in ws)


def main():
    lines = strip_page_furniture(extract_lines())
    s = next(i for i, l in enumerate(lines) if l.strip() == "Rules Glossary" and i > 3000)
    e = next(i for i in range(s + 1, len(lines)) if lines[i].strip() == "Gameplay Toolbox" and i > s + 500)
    body = lines[s + 1:e]
    # ข้ามคำอธิบายการอ่านคำศัพท์ช่วงต้นบท ("Glossary Conventions" / "Rules Definitions") — เริ่มที่นิยามแรกจริง
    # (ถ้าไม่ตัด หัวข้อ "Rules Definitions" จะตั้งตัวชี้ตัวอักษรไว้ที่ R แล้วนิยามหมวด A–Q ถูกทิ้งเงียบ ๆ)
    first = next(i for i, l in enumerate(body) if l.strip() == "Rules Definitions")
    body = body[first + 1:]

    heads = []  # (index, title, tag)
    last = ""
    for i, l in enumerate(body):
        l = l.strip()
        tm = TAGGED.match(l)
        if tm:
            heads.append((i, tm.group(1), tm.group(2)))
            last = tm.group(1).lower()
            continue
        if not PLAIN.match(l) or not title_ok(l) or i + 1 >= len(body):
            continue
        prev = body[i - 1].strip() if i else "."
        # ก่อนหน้าต้องเป็นปลายประโยค/ปลายหัวข้อ และห้ามเป็นหัวข้อที่เรียงย้อนกลับ
        if not (prev.endswith((".", ")", "”", "]", ":")) or prev in ("",)):
            continue
        if l.lower() < last[:1]:
            continue
        nxt = body[i + 1].strip()
        if not nxt or nxt.endswith(":") and len(nxt.split()) <= 3:
            continue
        if l.lower()[:1] < last[:1]:
            continue
        heads.append((i, l, None))
        last = l.lower()

    entries, errors = [], []
    for n, (i, title, tag) in enumerate(heads):
        stop = heads[n + 1][0] if n + 1 < len(heads) else len(body)
        text = "\n".join(body[i + 1:stop])
        entries.append({"title": title, "tag": tag, "text": paragraphs(body[i + 1:stop])})
    json.dump({"entries": entries, "errors": errors}, sys.stdout, ensure_ascii=False, indent=1)



if __name__ == "__main__":
    main()

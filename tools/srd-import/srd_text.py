"""ตัวช่วยอ่านข้อความที่ pdftotext -raw ดึงจาก SRD 5.2.1 — ใช้ร่วมกันทุกตัว parse

ทำไมต้อง -raw: `-layout` เลื่อนแถวของตารางในไฟล์นี้ (เจอมาแล้วตอนทำ Sprint H Part 4)
ทำไมต้อง -enc UTF-8: ค่าเริ่มต้นของ xpdf (pdftotext 4.00) บน Windows ไม่ใช่ UTF-8 ทำให้ en-dash/em-dash/×
กลายเป็น U+FFFD ~700 จุด (เช่น "4-6" กลายเป็น "4?6") — ใส่ -enc แล้วเหลือ 0 จุด (นับจริงตอนสร้างไฟล์นี้)
"""
import re
import subprocess
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PDF = REPO / "docs" / "SRD_CC_v5.2.1.pdf"

# หัวกระดาษที่แทรกกลางเนื้อหา: "291 System Reference Document 5.2.1" (หน้าคู่)
# และ "System Reference Document 5.2.1" ตามด้วยเลขหน้า (หน้าคี่)
_PAGE_HEADER = re.compile(r"^(\d+ )?System Reference Document 5\.2\.1$")
_LONE_PAGENUM = re.compile(r"^\d{1,3}$")


def extract_lines() -> list[str]:
    raw = subprocess.run(
        ["pdftotext", "-enc", "UTF-8", "-raw", str(PDF), "-"],
        capture_output=True, check=True,
    ).stdout
    text = raw.decode("utf-8")  # ไม่ใช้ errors="replace" — ถอดรหัสไม่ได้ต้องพังให้เห็น ไม่กลืนเงียบ
    assert "�" not in text, "พบตัวอักษรเสีย U+FFFD — encoding ของ pdftotext เพี้ยน"
    return text.replace("\r", "").split("\n")


def strip_page_furniture(lines: list[str]) -> list[str]:
    """ตัดหัวกระดาษ + เลขหน้า

    เลขหน้าเดี่ยว ๆ ตัดเฉพาะบรรทัดที่ตามหลังหัวกระดาษทันที — เลขเดี่ยวที่อื่นอาจเป็นเนื้อหาจริง
    (เช่นค่าในตาราง) ห้ามตัดมั่ว
    """
    res: list[str] = []
    i = 0
    while i < len(lines):
        if _PAGE_HEADER.match(lines[i].strip()):
            i += 2 if i + 1 < len(lines) and _LONE_PAGENUM.match(lines[i + 1].strip()) else 1
            continue
        res.append(lines[i])
        i += 1
    return res


def join_wrapped(lines: list[str]) -> str:
    """ต่อบรรทัดที่ถูกตัดกลางย่อหน้า + ต่อคำที่ถูกตัดด้วยขีด

    กฎขีด: บรรทัดลงท้าย "<ตัวอักษร>-" และบรรทัดถัดไปขึ้นต้นตัวพิมพ์เล็ก → ต่อคำโดยไม่เอาขีด
    ("Scim-" + "itar" → "Scimitar") ข้อจำกัดที่ยอมรับ: ขีดจริงในคำประสมที่ถูกตัดบรรทัดพอดี
    ("self-" + "control") จะถูกกลืนเป็น "selfcontrol"
    """
    out = ""
    for ln in lines:
        ln = ln.rstrip()
        if not out:
            out = ln
        elif re.search(r"[A-Za-z]-$", out) and re.match(r"^[a-z]", ln):
            out = out[:-1] + ln
        else:
            out += " " + ln
    return out.strip()


# บรรทัดขึ้นต้นด้วย "ชื่อหัวข้อย่อย. " เช่น "Exhaustion Levels. This condition…" / "Attacks Affected. …" = ย่อหน้าใหม่เสมอ
_LEAD_IN = re.compile(r"^[A-Z][A-Za-z0-9'’\- ]{2,40}\. [A-Z]")


_TITLE_SMALL = {"of", "the", "and", "or", "a", "an", "in", "to", "with", "on", "for", "from", "by", "at", "as"}


def _is_lead_in(line: str) -> bool:
    # ต้องเป็นหัวข้อจริง: ทุกคำขึ้นต้นตัวใหญ่/เลข (ยกเว้นคำเชื่อม) ไม่ใช่ประโยคธรรมดาที่ขึ้นต้นตัวใหญ่แล้วมีจุด
    # (เคยพลาด: "Rest removes 1 of your Exhaustion levels. When…" ถูกตัดเป็นย่อหน้าใหม่)
    m = _LEAD_IN.match(line)
    if not m:
        return False
    title = line[: line.index(". ")]
    return all(w[0].isupper() or w[0].isdigit() or w.lower() in _TITLE_SMALL for w in title.split())


def paragraphs(ls: list[str]) -> str:
    """จัดกลุ่มบรรทัดเป็นย่อหน้า (คั่นด้วยบรรทัดว่าง)

    ย่อหน้าใหม่เมื่อ: (ก) บรรทัดนี้ขึ้นต้นด้วยหัวข้อย่อยแบบ "Lead In. " หรือ
    (ข) บรรทัดก่อนหน้าจบประโยคและสั้นกว่าความกว้างคอลัมน์ปกติ (~50 ตัวอักษร) แล้วบรรทัดนี้ขึ้นต้นตัวใหญ่/เลข
    ข้อจำกัด: ตารางในเวท/กฎไม่ได้รับการจัดรูป — ข้อความครบตามลำดับที่ pdftotext อ่านได้ แต่โครงแถว/คอลัมน์หาย
    """
    paras, cur = [], []
    for l in ls:
        if cur and (
            _is_lead_in(l)
            or (re.search(r"[.!?:)”]$", cur[-1]) and re.match(r"^[A-Z0-9]", l) and len(cur[-1]) < 44)
        ):
            paras.append(join_wrapped(cur))
            cur = []
        cur.append(l)
    if cur:
        paras.append(join_wrapped(cur))
    return "\n\n".join(x for x in paras if x)

/**
 * Sprint G Part 9 — รูปร่างของ "ทำอะไรได้บ้าง" ที่ server คำนวณมาแล้ว
 * (backend: GET /campaigns/{cid}/sessions/{sid}/affordances/{characterId})
 *
 * ⚠️ ลิสต์นี้ **คือ** ตัวกรองความปลอดภัยแล้ว ไม่ใช่ข้อมูลดิบที่ client ต้องมากรองต่อ —
 * spatial-play-design §1.2 บอกว่ารายการ action เป็นช่องรั่วในตัวมันเอง (บอกว่า "โจมตีก็อบลินได้"
 * = บอกว่ามีก็อบลิน) server จึงคำนวณจาก "สิ่งที่คนนี้มองเห็น" เท่านั้น
 *
 * **ห้ามซ่อนรายการด้วย CSS หรือกรองซ้ำที่นี่** ถ้ามันมาถึง client แปลว่าคนนี้มีสิทธิ์เห็นแล้ว
 * และถ้าสิ่งที่ทำไม่ได้ยังโผล่มา แปลว่า backend พัง ไม่ใช่หน้าที่ frontend ปิดแผล
 */

/** โหมดการทอย — server ส่งมาเป็นสตริงตาม convention ของ API ทั้งโปรเจกต์ */
export type AdvantageMode = "None" | "Advantage" | "Disadvantage";

/** ราคาของท่าตาม action economy */
export type AffordanceCost = "Action" | "BonusAction" | "Reaction" | "None";

/** เหตุผลภาษาคนว่าทำไมตัวเลือกนี้มีราคา — `code` ไว้ให้ UI เลือกสี/ไอคอน ส่วน `message`
 * เป็นข้อความที่แสดงจริง (server เขียนมาให้แล้ว client ไม่ต้องแปลเอง) */
export interface AffordanceNote {
  code: string;
  message: string;
}

export interface AffordanceItem {
  optionId: string;
  optionName: string;
  targetId: string | null;
  targetName: string | null;
  advantageMode: AdvantageMode;
  cost: AffordanceCost;
  distanceFeet: number | null;
  notes: AffordanceNote[];
}

export interface AffordanceList {
  characterId: string;
  options: AffordanceItem[];
}

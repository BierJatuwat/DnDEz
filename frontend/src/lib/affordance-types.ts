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
  /**
   * Sprint H Part 7 — "Character" หรือ "Monster" · endpoint โจมตีรับได้เฉพาะตัวละคร (มอนสเตอร์ยังไม่มี
   * พลังชีวิตรายตัว) หน้าจอจึงต้องรู้ชนิดเป้าเพื่อไม่เสนอปุ่มที่กดแล้วพัง · optional เพราะ server รุ่นก่อน
   * Part นี้ไม่ส่งมา — ไม่มีค่า = ถือว่าเป็นตัวละครตามพฤติกรรมเดิม
   */
  targetEntityType?: string | null;
  advantageMode: AdvantageMode;
  cost: AffordanceCost;
  distanceFeet: number | null;
  notes: AffordanceNote[];

  // ── Sprint H Part 4 — สูตรที่ server ประกอบให้แล้ว (ActionFormulaResolver) ──
  // server คืน null เมื่อคำนวณให้อย่างปลอดภัยไม่ได้ (เช่นไม่รู้คลาสของผู้ร่าย, cantrip เลเวล 5+)
  // optional ด้วยเพราะ frontend กับ backend deploy แยกกัน — response จาก server รุ่นก่อน Part นี้
  // ไม่มีฟิลด์เหล่านี้เลย ต้องตีความเหมือน null ทุกประการ คือ "ไม่มีสูตร ให้ DM ทอยให้"

  /** ability ที่ใช้จริงหลัง resolve แล้ว เช่น "Strength" / "Wisdom" */
  ability?: string | null;
  /** สูตรทอยโจมตี เช่น "1d20+4" — มีเฉพาะท่าที่ทอยโจมตี */
  attackExpression?: string | null;
  /** DC ที่เป้าต้องเซฟให้ผ่าน — มีเฉพาะท่าแบบให้เป้าเซฟ */
  saveDc?: number | null;
  /** ability ที่เป้าใช้เซฟ เช่น "Dexterity" */
  saveAbility?: string | null;
  /** สูตรดาเมจรวม modifier แล้ว เช่น "1d8+2" */
  damageExpression?: string | null;
  /** ชนิดดาเมจตามชื่อใน DnDEz.Rules.DamageType เช่น "Slashing" */
  damageType?: string | null;
  /** สูตรการรักษารวม modifier แล้ว เช่น "2d8+2" */
  healingExpression?: string | null;
}

export interface AffordanceList {
  characterId: string;
  options: AffordanceItem[];
}

import type { AdvantageMode, AffordanceItem } from "./affordance-types";

/**
 * Sprint H Part 4 (frontend) — แปลงสูตรที่ server ประกอบมาให้ (ActionFormulaResolver) เป็นข้อความที่
 * ผู้เล่นอ่านได้ และตัดสินว่ากดทอยผ่านระบบได้เลย หรือต้องให้ DM ทอยให้ · ไม่มี React/fetch
 *
 * **ไม่คำนวณตัวเลขเองแม้แต่ตัวเดียว** — ทุกสูตรมาจาก server ตรง ๆ ไฟล์นี้แค่จัดรูปและจัดประเภท ·
 * ถ้า server ไม่ส่งสูตรมา (null หรือ server รุ่นเก่า) คำตอบคือ "ให้ DM ทอยให้" เสมอ ไม่เดา
 */

/** ชื่อไทยสั้น ๆ ของ 13 ชนิดตาม DnDEz.Rules.DamageType — ใช้ต่อท้ายสูตรดาเมจ ("1d8+2 ฟัน") */
const DAMAGE_TYPE_TH: Record<string, string> = {
  Acid: "กรด",
  Bludgeoning: "ทุบ",
  Cold: "เย็น",
  Fire: "ไฟ",
  Force: "พลังเวท",
  Lightning: "สายฟ้า",
  Necrotic: "มรณะ",
  Piercing: "แทง",
  Poison: "พิษ",
  Psychic: "จิต",
  Radiant: "แสง",
  Slashing: "ฟัน",
  Thunder: "คลื่นเสียง",
};

/** ชนิดที่ไม่รู้จักแสดงชื่อเดิม (ไม่ซ่อน) · ไม่มีชนิด → สตริงว่าง */
export function damageTypeLabel(type: string | null | undefined): string {
  if (!type) return "";
  return DAMAGE_TYPE_TH[type] ?? type;
}

const ABILITY_SHORT: Record<string, string> = {
  Strength: "STR",
  Dexterity: "DEX",
  Constitution: "CON",
  Intelligence: "INT",
  Wisdom: "WIS",
  Charisma: "CHA",
};

/** ตัวย่อที่ใช้บนชีท ("Dexterity" → "DEX") · ไม่มีค่า → สตริงว่าง */
export function abilityShort(ability: string | null | undefined): string {
  if (!ability) return "";
  return ABILITY_SHORT[ability] ?? ability;
}

/**
 * บรรทัดสูตรบนการ์ด — "ทอยโจมตี 1d20+4 · ดาเมจ 1d8+2 ฟัน" (mockup มีบรรทัด "สร้างดาเมจ 1d8+3")
 * แสดงให้เห็นก่อนกดเสมอ เพื่อให้ผู้เล่นเรียนรู้ตัวเลขไปด้วย · ไม่มีสูตรเลย → null
 */
export function formulaLine(item: AffordanceItem): string | null {
  const parts: string[] = [];

  if (item.attackExpression) {
    parts.push(`ทอยโจมตี ${item.attackExpression}`);
  }
  if (item.saveDc != null) {
    const ability = abilityShort(item.saveAbility);
    parts.push(`เป้าทอยเซฟ${ability ? ` ${ability}` : ""} ต้องได้ ${item.saveDc}`);
  }
  if (item.damageExpression) {
    const type = damageTypeLabel(item.damageType);
    parts.push(`ดาเมจ ${item.damageExpression}${type ? ` ${type}` : ""}`);
  }
  if (item.healingExpression) {
    parts.push(`รักษา ${item.healingExpression}`);
  }

  return parts.length > 0 ? parts.join(" · ") : null;
}

/** รูปเดียวที่ server ส่งมา ("1d20+4", "1d8-1", "1d10", "3d4+3") — รูปอื่นไม่แปล ไม่เดาความหมาย */
const SIMPLE_DICE = /^(\d+)d(\d+)(?:([+-])(\d+))?$/;

/** แปลสูตรเป็นภาษาคนให้มือใหม่ — "1d20+4" → "ลูกเต๋า 20 หน้า 1 ลูก แล้วบวก 4" */
export function describeDice(expression: string): string | null {
  const match = SIMPLE_DICE.exec(expression);
  if (!match) return null;

  const [, count, sides, sign, amount] = match;
  const dice = `ลูกเต๋า ${sides} หน้า ${count} ลูก`;
  if (!sign) return dice;
  return `${dice} แล้ว${sign === "+" ? "บวก" : "ลบ"} ${amount}`;
}

/**
 * กดแล้วทอยผ่านระบบได้เลย (`attack`) หรือต้องให้ DM ทอยให้ (`dm`)
 *
 * endpoint ที่ใช้ได้ตอนนี้มีแค่ `POST .../attacks` ซึ่งต้องมีทั้งสูตรทอยโจมตี สูตรดาเมจ และชนิดดาเมจ ·
 * ท่าแบบอื่นยังไม่มี endpoint รองรับ (ตรวจแล้วตอน Sprint H Part 4 backend: API มีแค่ /checks กับ
 * /attacks ไม่มีทางรักษา ไม่มีทางให้เป้าเซฟ) จึงบอกเหตุผลแล้วให้ DM ทอยให้ — **ไม่เปิดช่องกรอก**
 * เพราะช่องกรอกมือมีเฉพาะหน้า DM (player-side-design §5)
 *
 * ถ้าไม่รู้ชนิดดาเมจจะไม่ส่งไปเดา — โค้ดเดิมส่ง "Slashing" ตายตัวทุกท่า ธนูจึงถูกบันทึกเป็นดาเมจฟัน
 *
 * เป้าที่เป็นมอนสเตอร์ (`targetEntityType === "Monster"`) ก็ให้ DM ทอยให้เช่นกัน — `/attacks` รับได้
 * เฉพาะเป้าที่เป็นตัวละคร (มอนสเตอร์ยังไม่มีพลังชีวิตรายตัว สเตตบล็อกเป็นแค่ template ระดับแคมเปญ)
 * ค่าอื่นทั้งหมด ("Character", null, undefined — รวมถึง server รุ่นเก่าที่ไม่ส่งชนิดมา) ยังทอยได้ตามเดิม
 */
export type AttackPlan =
  | {
      kind: "attack";
      targetId: string;
      expression: string;
      damageExpression: string;
      damageType: string;
      advantage: AdvantageMode;
    }
  | { kind: "dm"; reason: string };

export function attackPlan(item: AffordanceItem): AttackPlan {
  if (!item.targetId) {
    return { kind: "dm", reason: "ท่านี้ไม่ได้เล็งใคร ระบบยังไม่มีปุ่มทอยให้ — บอก DM ว่าจะทำอะไร" };
  }

  if (item.targetEntityType === "Monster") {
    return {
      kind: "dm",
      reason: "เป้าเป็นมอนสเตอร์ ระบบยังทอยโจมตีใส่มอนสเตอร์ไม่ได้ — บอก DM ให้ทอยให้",
    };
  }

  if (item.attackExpression && item.damageExpression && item.damageType) {
    return {
      kind: "attack",
      targetId: item.targetId,
      expression: item.attackExpression,
      damageExpression: item.damageExpression,
      damageType: item.damageType,
      advantage: item.advantageMode,
    };
  }

  if (item.saveDc != null) {
    const ability = abilityShort(item.saveAbility);
    return {
      kind: "dm",
      reason: `เป้าต้องทอยเซฟ${ability ? ` ${ability}` : ""} เอง ระบบยังทอยแทนเป้าไม่ได้ — บอก DM ให้ทอยให้`,
    };
  }

  if (item.healingExpression) {
    return { kind: "dm", reason: "ระบบยังไม่มีปุ่มรักษา — บอก DM ให้เพิ่มพลังชีวิตให้ตามสูตรนี้" };
  }

  if (item.damageExpression && !item.attackExpression) {
    return {
      kind: "dm",
      reason: "ท่านี้โดนเป้าอัตโนมัติไม่ต้องทอยโจมตี แต่ระบบยังไม่มีปุ่มสำหรับท่าแบบนี้ — บอก DM ให้ทอยดาเมจให้",
    };
  }

  return {
    kind: "dm",
    reason: "ระบบคำนวณสูตรของท่านี้ให้ไม่ได้ (เช่นตัวละครยังไม่ได้ระบุคลาส) — บอก DM ให้ทอยให้",
  };
}

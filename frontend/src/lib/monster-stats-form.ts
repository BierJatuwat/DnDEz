import type { CampaignMonster } from "./srd-types";
import type { MonsterStatChanges } from "./use-srd";

/**
 * ตรรกะของฟอร์มแก้ค่าพลังมอนสเตอร์ — ไม่มี React แยกมาให้เทสต์ได้ตรง ๆ
 *
 * ⚠️ **ด่านนี้ไม่ใช่ตัวกันค่าผิด** — server validate เองทุกค่าและฐานข้อมูลมี CHECK เป็นชั้นสุดท้าย
 * (`ck_monster_stat_block_numbers`) ที่นี่มีไว้บอกผู้ใช้ทันทีว่าพิมพ์อะไรผิดตรงช่องไหน แทนที่จะต้องกดบันทึก
 * แล้วรอ 400 กลับมา · ขอบเขตต้องตรงกับฝั่ง C# (`MonsterEndpoints`) — แก้ฝั่งไหนต้องมาแก้ที่นี่ด้วย
 */

export const STAT_LIMITS = {
  /** AC สูงสุดใน SRD คือ 25 (Tarrasque) — เพดานนี้กันพิมพ์พลาด ไม่ใช่กฎของเกม */
  armorClass: { min: 1, max: 99 },
  /** HP สูงสุดใน SRD คือ 697 (Tarrasque) */
  hitPoints: { min: 1, max: 9999 },
  /** 0 ถูกต้องได้ — สิ่งมีชีวิตที่ขยับเองไม่ได้ */
  speedFeet: { min: 0, max: 999 },
  /** SRD 5.2 "Reach": ทุกสิ่งมีชีวิตเอื้อมได้อย่างน้อย 5 ฟุต · ยาวสุดในเล่มคือ 60 ฟุต (Roper) */
  reachFeet: { min: 5, max: 999 },
} as const;

export type StatField = keyof typeof STAT_LIMITS;

/** ค่าที่อยู่ในช่องกรอก — เก็บเป็นสตริงเพราะระหว่างพิมพ์ยังเป็นค่าที่ยังไม่สมบูรณ์ได้ (เช่น "" หรือ "-") */
export type MonsterStatDraft = {
  name: string;
  armorClass: string;
  hitPoints: string;
  speedFeet: string;
  reachFeet: string;
};

export type StatDraftResult = {
  /** เฉพาะช่องที่ต่างจากค่าเดิมจริง — ช่องที่ไม่แตะจะไม่ถูกส่งไป (PATCH ไม่ใช่ PUT) */
  changes: MonsterStatChanges;
  /** ข้อความผิดพลาดต่อช่อง — ว่าง = ผ่านหมด */
  errors: Partial<Record<StatField | "name", string>>;
};

/** เติมฟอร์มจากค่าปัจจุบันของแม่แบบ */
export function draftFromMonster(monster: CampaignMonster): MonsterStatDraft {
  return {
    name: monster.name,
    armorClass: String(monster.armorClass),
    hitPoints: String(monster.hitPoints),
    speedFeet: String(monster.speedFeet),
    reachFeet: String(monster.reachFeet),
  };
}

const FIELD_LABEL: Record<StatField, string> = {
  armorClass: "ค่าเกราะ",
  hitPoints: "พลังชีวิต",
  speedFeet: "ระยะเดิน",
  reachFeet: "ระยะเอื้อม",
};

/**
 * ตรวจฟอร์มแล้วคืนเฉพาะสิ่งที่เปลี่ยนจริง
 *
 * ค่าที่ "ไม่เปลี่ยน" ต้องไม่ถูกส่งไป ไม่ใช่เพราะประหยัดไบต์ แต่เพราะการส่งค่าเดิมกลับไปทุกครั้งทำให้
 * แยกไม่ออกว่า DM ตั้งใจแก้ช่องไหน — ถ้าวันหนึ่งมี log การแก้ค่า มันจะเต็มไปด้วยการแก้ที่ไม่เคยเกิดขึ้น
 */
export function parseStatDraft(draft: MonsterStatDraft, original: CampaignMonster): StatDraftResult {
  const changes: MonsterStatChanges = {};
  const errors: StatDraftResult["errors"] = {};

  const name = draft.name.trim();
  if (name.length === 0) {
    errors.name = "ต้องมีชื่อ";
  } else if (name.length > 200) {
    errors.name = "ชื่อยาวเกินไป";
  } else if (name !== original.name) {
    changes.name = name;
  }

  for (const field of Object.keys(STAT_LIMITS) as StatField[]) {
    const raw = draft[field].trim();
    const { min, max } = STAT_LIMITS[field];

    if (raw === "") {
      errors[field] = "ใส่ตัวเลข";
      continue;
    }

    // `Number()` รับ "1e3" กับ "0x10" ด้วย ซึ่งไม่ใช่สิ่งที่คนตั้งใจพิมพ์ในช่องค่าพลัง — บังคับรูปเลขจำนวนเต็ม
    if (!/^-?\d+$/.test(raw)) {
      errors[field] = "ต้องเป็นจำนวนเต็ม";
      continue;
    }

    const value = Number(raw);
    if (value < min || value > max) {
      errors[field] = `${FIELD_LABEL[field]}ต้องอยู่ระหว่าง ${min}–${max}`;
      continue;
    }

    if (value !== original[field]) {
      changes[field] = value;
    }
  }

  return { changes, errors };
}

export function hasErrors(result: StatDraftResult): boolean {
  return Object.keys(result.errors).length > 0;
}

export function hasChanges(result: StatDraftResult): boolean {
  return Object.keys(result.changes).length > 0;
}

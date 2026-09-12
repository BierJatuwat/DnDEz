import type { AffordanceCost, AffordanceItem, AdvantageMode } from "./affordance-types";

/**
 * Sprint G Part 9 — ตรรกะแสดงผลของพาเนล "คุณจะทำอะไร?" (ไม่มี React/fetch) แยกมาให้เทสต์ได้ตรง ๆ
 * ตามธรรมเนียมเดิมของโปรเจกต์
 */

/** ป้ายราคาบนมุมขวาของการ์ด ตาม mockup uxpilot-player-view.png ("1 Action" / "Bonus Action") */
export function costLabel(cost: AffordanceCost): string {
  switch (cost) {
    case "Action":
      return "1 Action";
    case "BonusAction":
      return "Bonus Action";
    case "Reaction":
      return "Reaction";
    case "None":
      return "ไม่ใช้ action";
  }
}

/** ป้ายมุมบนของการ์ดเมื่อมี advantage/disadvantage — คืน null เมื่อทอยปกติ (ไม่ต้องมีป้าย) */
export function advantageBadge(mode: AdvantageMode): string | null {
  if (mode === "Disadvantage") return "DISADVANTAGE";
  if (mode === "Advantage") return "ADVANTAGE";
  return null;
}

/**
 * บรรทัดสรุปใต้ชื่อท่า — "ก็อบลินยาม · ห่าง 5 ฟุต" (เป้ากับระยะ)
 *
 * บรรทัดสูตรเต๋า ("ทอยโจมตี 1d20+4 · ดาเมจ 1d8+2 ฟัน") แยกไปอยู่ `formulaLine` ใน
 * affordance-formula.ts ตั้งแต่ Sprint H Part 4 ที่ catalog มีดาเมจ/ability แล้ว
 */
export function targetLine(item: AffordanceItem): string | null {
  if (!item.targetName) return null;
  return item.distanceFeet === null
    ? item.targetName
    : `${item.targetName} · ห่าง ${item.distanceFeet} ฟุต`;
}

/**
 * จัดกลุ่มตัวเลือกตามท่า เพื่อให้การ์ดหนึ่งใบ = หนึ่งท่า แล้วในนั้นค่อยเลือกเป้า
 *
 * server ส่งมาเป็น "ท่า × เป้า" (ดาบยาว→ก็อบลิน, ดาบยาว→ออร์ค เป็นคนละแถว) ซึ่งถูกต้องสำหรับ engine
 * แต่ถ้าเรนเดอร์ตรง ๆ ผู้เล่นที่มีศัตรู 4 ตัวจะเห็นการ์ด "ดาบยาว" ซ้ำ 4 ใบ — mockup แสดงหนึ่งท่า
 * ต่อหนึ่งใบ · **จัดกลุ่มเพื่อการนำเสนอเท่านั้น ไม่ได้กรองอะไรทิ้ง** ทุกเป้ายังอยู่ครบในกลุ่ม
 *
 * ลำดับกลุ่มยึดตามลำดับที่ server ส่งมา (ไม่เรียงใหม่) เพื่อให้ผลลัพธ์คงที่ระหว่าง re-render
 */
export function groupByOption(items: AffordanceItem[]): AffordanceOptionGroup[] {
  const groups = new Map<string, AffordanceOptionGroup>();

  for (const item of items) {
    const existing = groups.get(item.optionId);
    if (existing) {
      existing.targets.push(item);
      continue;
    }
    groups.set(item.optionId, {
      optionId: item.optionId,
      optionName: item.optionName,
      cost: item.cost,
      targets: [item],
    });
  }

  return [...groups.values()];
}

export interface AffordanceOptionGroup {
  optionId: string;
  optionName: string;
  cost: AffordanceCost;
  targets: AffordanceItem[];
}

/**
 * ทรัพยากรที่ใช้ไปแล้วในเทิร์นนี้ — ใช้เรนเดอร์การ์ดสถานะ "ใช้ไปแล้ว (เทาจาง)" ของ mockup
 *
 * **ทำไมต้องคำนวณจาก turn state ไม่ใช่จากลิสต์ affordance**: ตามโมเดล 3 ระดับ (§1.4) ท่าที่
 * ทรัพยากรหมดแล้วคือ "ระดับ 1 = ไม่อยู่ในรายการเลย" server จึงตัดทิ้งตั้งแต่ต้นทาง ลิสต์ที่มาถึงจึง
 * ไม่มีทางบอกได้ว่า "เมื่อกี้มีท่าอะไรบ้างที่หายไปเพราะใช้ action ไปแล้ว" · แต่ "ฉันใช้ action ไปแล้ว"
 * เป็นข้อมูลของตัวเอง ไม่ใช่ความลับ และเป็นคำอธิบายที่ผู้เล่นต้องการพอดีเมื่อเห็นรายการหดลง
 * จึงบอกจาก turn state แทน — ได้ผลเดียวกับ mockup โดยไม่ต้องให้ server ส่งของที่ทำไม่ได้ออกมา
 */
export function spentResourceLabels(turnState: {
  actionAvailable: boolean;
  bonusActionAvailable: boolean;
  reactionAvailable: boolean;
} | null): string[] {
  if (!turnState) return [];

  const spent: string[] = [];
  if (!turnState.actionAvailable) spent.push("Action");
  if (!turnState.bonusActionAvailable) spent.push("Bonus Action");
  if (!turnState.reactionAvailable) spent.push("Reaction");
  return spent;
}

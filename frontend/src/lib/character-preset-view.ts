import type { PresetAbilityScores } from "./character-preset-types";

/**
 * Sprint G Part 7 — ตรรกะแสดงผลของการ์ด preset (ไม่มี React/fetch) แยกออกมาให้เทสต์ได้ตรง ๆ
 * ตามธรรมเนียมเดิมของโปรเจกต์ (lib/ มีเทสต์, component เป็นการนำเสนออย่างเดียว)
 */

/** ค่า modifier มาตรฐาน 5e: floor((score-10)/2) — ใช้ทั้งแสดงผลการ์ดและคำนวณ initiative ฝั่ง API */
export function abilityModifier(score: number): number {
  return Math.floor((score - 10) / 2);
}

/** "+3" หรือ "-1" — ป้าย modifier ต้องมีเครื่องหมายเสมอ (0 แสดงเป็น "+0" ไม่ใช่ "0" เฉย ๆ
 * เพื่อให้อ่านออกทันทีว่าเป็นค่าบวก/ลบ ไม่ต้องนับเครื่องหมายลบที่หายไป) */
export function formatModifier(modifier: number): string {
  return modifier >= 0 ? `+${modifier}` : `${modifier}`;
}

const ABILITY_LABELS: { key: keyof PresetAbilityScores; label: string }[] = [
  { key: "strength", label: "STR" },
  { key: "dexterity", label: "DEX" },
  { key: "constitution", label: "CON" },
  { key: "intelligence", label: "INT" },
  { key: "wisdom", label: "WIS" },
  { key: "charisma", label: "CHA" },
];

/**
 * ค่าความสามารถ 2 อันดับแรกของ preset (เรียงจากสูงไปต่ำ) — การ์ดในรูป mockup โชว์แค่ 2 ค่าเด่นต่อ
 * ตัว (เช่นนักรบโชว์ STR/DEX ไม่โชว์ครบ 6 ค่า) เพราะเป็นการ์ดสรุปเร็ว ๆ ไม่ใช่ sheet เต็ม
 *
 * เท่ากันแล้วเรียงยังไง: ตามลำดับ STR→CHA คงที่เสมอ (ไม่ใช่สุ่ม) กันการ์ดหน้าตาสลับไปมาระหว่าง
 * re-render ของ preset เดียวกัน
 */
export function topAbilityScores(
  scores: PresetAbilityScores | null,
  count = 2,
): { label: string; value: number; modifier: string }[] {
  if (!scores) return [];

  return [...ABILITY_LABELS]
    .sort((a, b) => scores[b.key] - scores[a.key])
    .slice(0, count)
    .map(({ key, label }) => ({
      label,
      value: scores[key],
      modifier: formatModifier(abilityModifier(scores[key])),
    }));
}

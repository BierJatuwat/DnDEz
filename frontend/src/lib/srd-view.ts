import type {
  SrdCorpusItem,
  SrdMonsterAction,
  SrdMonsterDetail,
  SrdMonsterSummary,
} from "./srd-types";
import type { SceneToken } from "./scene-types";

/**
 * ตรรกะแสดงผลของคลัง SRD (ไม่มี React/fetch) — แยกมาให้เทสต์ได้ตรง ๆ ตามธรรมเนียมของโปรเจกต์
 * (lib/ มีเทสต์, component เป็นการนำเสนออย่างเดียว)
 */

/** ตัวเลือกความยากที่ให้ DM กดกรอง — ค่าเป็นสตริงเดียวกับที่ API รับ (`crMax`) */
export const CR_FILTERS: { value: string; label: string }[] = [
  { value: "1/4", label: "มือใหม่ (ถึง 1/4)" },
  { value: "1", label: "เริ่มต้น (ถึง 1)" },
  { value: "5", label: "กลาง (ถึง 5)" },
  { value: "10", label: "สูง (ถึง 10)" },
  { value: "", label: "ทั้งหมด" },
];

/**
 * "1/8" → 0.125 · "3" → 3 · อ่านไม่ออก → null
 *
 * CR เป็นข้อความเพราะ SRD ใช้เศษส่วนจริง ๆ ("1/8" ไม่ใช่ "0.125") — ฟังก์ชันนี้ใช้เฉพาะตอนเทียบ/เรียง
 * ห้ามเอาค่าที่ได้ไปเก็บกลับหรือส่งไป API (จะกลายเป็น "0.125" ซึ่งไม่มีในตาราง)
 */
export function challengeRatingValue(cr: string): number | null {
  const text = cr.trim();
  // `Number("")` คือ 0 ไม่ใช่ NaN — ถ้าไม่ดักตรงนี้ CR ว่างจะถูกอ่านเป็น 0 แล้วกลายเป็น "อ่อน" เงียบ ๆ
  if (text === "") return null;
  const slash = text.indexOf("/");
  if (slash > 0) {
    const numerator = Number(text.slice(0, slash));
    const denominator = Number(text.slice(slash + 1));
    if (!Number.isFinite(numerator) || !Number.isFinite(denominator) || denominator === 0) return null;
    return numerator / denominator;
  }
  const whole = Number(text);
  return Number.isFinite(whole) ? whole : null;
}

/**
 * ป้ายความยากเป็นภาษาคน — DM มือใหม่ไม่รู้ว่า "CR 1/4" แปลว่าอะไร แต่รู้ว่า "อ่อน" กับ "อันตรายมาก"
 * ต่างกันยังไง · ยังโชว์ตัวเลข CR จริงคู่กันเสมอ (คนที่รู้กฎอยู่แล้วต้องอ่านค่าจริงได้)
 *
 * เกณฑ์แบ่ง: เป็นดุลยพินิจของโปรเจกต์นี้เอง **ไม่ใช่ตารางจาก SRD** — SRD ไม่มีคำบรรยาย CR เป็นคำพูด
 */
export function difficultyLabel(cr: string): string {
  const value = challengeRatingValue(cr);
  if (value === null) return "ไม่ระบุ";
  if (value < 1) return "อ่อน";
  if (value <= 4) return "พอสู้ได้";
  if (value <= 10) return "อันตราย";
  return "อันตรายมาก";
}

/** สีของป้ายความยาก — ใช้ token ของ Tailwind ที่หน้าอื่นใช้อยู่แล้ว */
export function difficultyTone(cr: string): "muted" | "green" | "amber" | "red" {
  const value = challengeRatingValue(cr);
  if (value === null) return "muted";
  if (value < 1) return "green";
  if (value <= 4) return "amber";
  return "red";
}

/** เรียงจากง่ายไปยาก แล้วเรียงชื่อเมื่อ CR เท่ากัน — ลำดับต้องคงที่เสมอ ไม่สลับไปมาระหว่าง re-render */
export function sortByDifficulty<T extends { challengeRating: string; name: string }>(rows: T[]): T[] {
  return [...rows].sort((a, b) => {
    const diff = (challengeRatingValue(a.challengeRating) ?? 0) - (challengeRatingValue(b.challengeRating) ?? 0);
    return diff !== 0 ? diff : a.name.localeCompare(b.name);
  });
}

/** "ขนาดกลาง · Beast · CR 1/4" — บรรทัดสรุปใต้ชื่อในการ์ด */
export function monsterSubtitle(monster: SrdMonsterSummary): string {
  return [monster.size, monster.type, `CR ${monster.challengeRating}`].filter(Boolean).join(" · ");
}

/**
 * ท่าทั้งหมดของ stat block เรียงตามลำดับที่อ่านในหนังสือ (คุณสมบัติ → แอ็กชัน → โบนัส → รีแอ็กชัน → เลเจนดารี)
 * พร้อมชื่อหมวดภาษาไทย · หมวดที่ว่างถูกตัดทิ้ง ไม่แสดงหัวข้อเปล่า
 */
export function monsterActionGroups(
  detail: SrdMonsterDetail | null,
): { kind: string; label: string; entries: SrdMonsterAction[] }[] {
  if (!detail) return [];
  const groups: { kind: keyof SrdMonsterDetail["data"]; label: string }[] = [
    { kind: "traits", label: "คุณสมบัติ" },
    { kind: "actions", label: "ท่าโจมตี / แอ็กชัน" },
    { kind: "bonusActions", label: "โบนัสแอ็กชัน" },
    { kind: "reactions", label: "รีแอ็กชัน" },
    { kind: "legendaryActions", label: "ท่าระดับตำนาน" },
  ];

  return groups
    .map(({ kind, label }) => ({
      kind: kind as string,
      label,
      entries: (detail.data[kind] as SrdMonsterAction[] | undefined) ?? [],
    }))
    .filter((g) => g.entries.length > 0);
}

const ABILITY_ORDER: { key: string; label: string }[] = [
  { key: "str", label: "STR" },
  { key: "dex", label: "DEX" },
  { key: "con", label: "CON" },
  { key: "int", label: "INT" },
  { key: "wis", label: "WIS" },
  { key: "cha", label: "CHA" },
];

/** ค่าความสามารถ 6 ช่องเรียงตามลำดับมาตรฐานเสมอ — ค่าที่ขาดไปถูกข้าม ไม่แสดงเป็น 0 (0 คือค่าที่เป็นไปไม่ได้) */
export function abilityRows(
  detail: SrdMonsterDetail | null,
): { label: string; score: number; modifier: string }[] {
  if (!detail?.data?.abilities) return [];
  return ABILITY_ORDER.flatMap(({ key, label }) => {
    const ability = detail.data.abilities[key];
    return ability ? [{ label, score: ability.score, modifier: signed(ability.mod) }] : [];
  });
}

/** "+2" / "-1" / "+0" — เครื่องหมายต้องมีเสมอเพื่อให้อ่านออกทันทีว่าบวกหรือลบ */
export function signed(value: number): string {
  return value >= 0 ? `+${value}` : `${value}`;
}

/** ระดับเวทเป็นภาษาคน — ระดับ 0 ใน SRD คือ cantrip ไม่ใช่ "เวทระดับศูนย์" */
export function spellLevelLabel(level: number | undefined): string {
  if (level === undefined) return "";
  return level === 0 ? "Cantrip" : `ระดับ ${level}`;
}

/** ชื่อหมวดของคลังกฎที่แสดงให้ผู้ใช้เห็น */
export function corpusCategoryLabel(category: string): string {
  if (category === "spell") return "เวท";
  if (category === "condition") return "สถานะ";
  if (category === "rule") return "กฎ";
  return category;
}

/** บรรทัดสรุปใต้ชื่อรายการในคลังกฎ — เวทบอกระดับ/สำนัก/คลาส · ที่เหลือบอกป้ายกำกับ */
export function corpusSubtitle(item: SrdCorpusItem): string {
  if (item.category === "spell") {
    const classes = item.data.classes?.join(", ");
    return [spellLevelLabel(item.data.level), item.data.school, classes].filter(Boolean).join(" · ");
  }
  return item.data.tag ?? corpusCategoryLabel(item.category);
}

/**
 * ชื่อที่จะเขียนบน token
 *
 * server เป็นคนบอกชื่อมาตั้งแต่รอบที่เพิ่มคลังมอนสเตอร์ (`TokenResponse.Name`) เพราะมอนสเตอร์ไม่ได้อยู่ใน
 * ลิสต์ตัวละครที่หน้าจอถืออยู่ — ค่อย fallback ไปหาในลิสต์ตัวละครเมื่อ server รุ่นเก่าไม่ส่งชื่อมา
 * และท้ายที่สุดใช้คำกลาง ๆ ("มอนสเตอร์") แทนการโชว์คำว่า "Monster" ซึ่งเป็นชื่อ enum ของ API ไม่ใช่ภาษาคน
 */
export function tokenLabel(
  token: Pick<SceneToken, "entityId" | "entityType"> & { name?: string | null },
  nameOf: (entityId: string) => string | undefined,
): string {
  return token.name ?? nameOf(token.entityId) ?? (token.entityType === "Monster" ? "มอนสเตอร์" : "ไม่ทราบชื่อ");
}

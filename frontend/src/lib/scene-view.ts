import type { CharacterSummary } from "./campaign-types";
import type { FogArea, SceneToken } from "./scene-types";

/**
 * Sprint G Part 6 — ตรรกะล้วนของการแสดงผล scene (ไม่มี React/fetch) แยกออกมาเพื่อเทสต์ได้ตรง ๆ
 * ตามธรรมเนียมของโปรเจกต์นี้ (lib/ มีเทสต์, component เป็นการนำเสนออย่างเดียว)
 */

/** ผลรวมพลังชีวิตของปาร์ตี้สำหรับจอกลาง — "42 / 50" ในรูป mockup */
export type PartyHealth = { current: number; max: number; memberCount: number };

/**
 * รวม HP ของ **ตัวละครผู้เล่นเท่านั้น** (`controlledByDm === false`) — NPC/มอนสเตอร์ที่ DM คุมไม่นับ
 * เข้าพลังชีวิตปาร์ตี้ (design doc §13.1: จอกลางแสดง "HP รวมทั้งปาร์ตี้")
 *
 * ตัวที่ HP เป็น null (ถูกซ่อน) ถูกข้ามทั้งตัว ไม่นับเป็น 0 — นับเป็น 0 จะทำให้จอกลางแสดงพลังชีวิต
 * ต่ำกว่าความจริงจนคนอ่านเข้าใจผิดว่าปาร์ตี้ใกล้ตาย · ในทางปฏิบัติตัวละครผู้เล่นไม่เคยถูกซ่อน HP
 * อยู่แล้ว (§13.1 ล็อกว่าเป็นตัวเลขจริงทั้ง 3 มุมมอง) การกันไว้ตรงนี้เป็นการกันเคสที่ไม่ควรเกิด
 */
export function partyHealth(characters: CharacterSummary[]): PartyHealth {
  const party = characters.filter(
    (c) => !c.controlledByDm && c.currentHp !== null && c.maxHp !== null,
  );

  return {
    current: party.reduce((sum, c) => sum + (c.currentHp ?? 0), 0),
    max: party.reduce((sum, c) => sum + (c.maxHp ?? 0), 0),
    memberCount: party.length,
  };
}

/**
 * จุดนี้อยู่ในพื้นที่ที่เปิดแล้วไหม — **สูตรเดียวกับ `SceneEndpoints.IsWithinArea` ฝั่ง C# เป๊ะ**
 * (รวมถึง `<=` ที่ขอบพอดีนับว่าเห็น) เพราะฝั่งนี้ใช้ตอบคำถามคนละข้อกับฝั่ง server:
 *
 * - **server**: "จะส่ง token นี้ให้คนนี้ไหม" (ตัวกันข้อมูลรั่วจริง)
 * - **client (ที่นี่)**: "จะวาดหมอกคลุมตรงไหน" + "DM ควรรู้ว่า token ตัวนี้ผู้เล่นยังไม่เห็น"
 *
 * ⚠️ ห้ามเข้าใจผิดว่าฟังก์ชันนี้เป็นตัวกันข้อมูล — ผู้เล่นไม่เคยได้รับ token ที่อยู่นอกพื้นที่เปิด
 * มาตั้งแต่ต้นอยู่แล้ว (server กรองทิ้งก่อนส่ง) ที่นี่ใช้กับข้อมูลที่ได้รับมาแล้วเท่านั้น
 */
export function isPointRevealed(x: number, y: number, areas: FogArea[]): boolean {
  return areas.some((a) => {
    const dx = x - a.x;
    const dy = y - a.y;
    return dx * dx + dy * dy <= a.radius * a.radius;
  });
}

/** กรอบพิกัดที่ครอบเนื้อหาทั้งหมด — ใช้ตั้ง viewBox ตอนไม่มีรูปแผนที่ให้อ้างอิงขนาด */
export type SceneBounds = { minX: number; minY: number; width: number; height: number };

const FALLBACK_BOUNDS: SceneBounds = { minX: 0, minY: 0, width: 100, height: 100 };

/**
 * คำนวณกรอบจาก token + พื้นที่ที่เปิดแล้ว พร้อมเผื่อขอบ — ใช้เฉพาะตอน scene **ไม่มีรูปแผนที่**
 * (มีรูปเมื่อไหร่ให้ยึดขนาดรูปเป็นระบบพิกัดแทน เพราะ x/y ของ token คือพิกัดพิกเซลบนรูปนั้น)
 *
 * scene ที่ว่างเปล่าจริง ๆ (ไม่มีทั้ง token และพื้นที่เปิด) คืนกรอบ 100x100 เพื่อให้ SVG ยังวาดได้
 * ไม่ใช่ viewBox ที่กว้าง 0 (ซึ่งทำให้ทั้งแผนที่หายไปเงียบ ๆ)
 */
/**
 * กรอบที่ครอบเนื้อหาทั้งหมด ใช้ตอนไม่มีรูปแผนที่
 *
 * ⚠️ **หน่วยเป็น "ช่อง" ไม่ใช่พิกเซล** — ดีฟอลต์ padding เคยเป็น 40 ตอนที่ทั้งไฟล์ยังตีความ x/y
 * เป็นพิกเซล พอ Sprint G Part 10 เปลี่ยนมาเป็นช่อง ค่า 40 กลายเป็น "เผื่อขอบ 40 ช่อง" = 2000 พิกเซล
 * ทำให้ token เล็กจนมองไม่เห็น (เจอจริงตอนเปิดดูบนเบราว์เซอร์) · เผื่อ 1 ช่องพอ
 */
export function sceneBounds(tokens: SceneToken[], areas: FogArea[], padding = 1): SceneBounds {
  const xs: number[] = [];
  const ys: number[] = [];

  for (const t of tokens) {
    xs.push(t.x);
    ys.push(t.y);
  }
  for (const a of areas) {
    xs.push(a.x - a.radius, a.x + a.radius);
    ys.push(a.y - a.radius, a.y + a.radius);
  }

  if (xs.length === 0) return FALLBACK_BOUNDS;

  const minX = Math.min(...xs) - padding;
  const minY = Math.min(...ys) - padding;
  const width = Math.max(...xs) + padding - minX;
  const height = Math.max(...ys) + padding - minY;

  return {
    minX,
    minY,
    // กันกรณี token ตัวเดียว/ซ้อนกันหมด ทำให้กว้างหรือสูงเป็น 0
    width: width > 0 ? width : FALLBACK_BOUNDS.width,
    height: height > 0 ? height : FALLBACK_BOUNDS.height,
  };
}

/** ตัวอักษรบน token — ตัวแรกของชื่อตัวละคร (เหมือน "A" ของ Aldric ในรูป mockup) */
export function tokenInitial(name: string | undefined): string {
  const trimmed = (name ?? "").trim();
  if (trimmed.length === 0) return "?";
  return [...trimmed][0].toUpperCase();
}

/** สัดส่วนพลังชีวิตเป็น 0..1 — ใช้เลือกสีแถบ HP (คืน null เมื่อไม่มีข้อมูล/max เป็น 0) */
export function healthRatio(current: number | null, max: number | null): number | null {
  if (current === null || max === null || max <= 0) return null;
  return Math.min(Math.max(current / max, 0), 1);
}

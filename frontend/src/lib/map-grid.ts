/**
 * เส้นกริดบนแผนที่ — ตรรกะล้วน ไม่มี React/SVG แยกมาให้เทสต์ได้ตรง ๆ
 *
 * หน่วยเป็น "พิกัดในภาพ" เหมือน `map-camera.ts` (ระบบเดียวกับ `viewBox` ของ SVG)
 */

/**
 * เพดานจำนวนเส้นต่อแกน — เกินนี้คือกริดถี่จนกลายเป็นพื้นสีทึบ ไม่ได้บอกอะไรเพิ่ม แต่กิน DOM node เป็นพัน
 *
 * เกิดได้จริงถ้า DM ตั้ง "พิกเซลต่อช่อง" ผิดเป็นเลขน้อย ๆ (เช่น 5 บนแผนที่ 4000 พิกเซล = 800 เส้น)
 * ซึ่งเป็นการพิมพ์ผิดที่เกิดง่ายมากเพราะช่องนั้นให้กรอกตัวเลขอิสระ
 */
export const MAX_GRID_LINES_PER_AXIS = 200;

/**
 * ตำแหน่งของเส้นกริดในช่วง [from, to] ที่เรียงตาม `offset + k * step`
 *
 * คืนลิสต์ว่างเมื่อ step ใช้ไม่ได้ (0/ติดลบ/ไม่ใช่ตัวเลข) หรือถี่เกินเพดาน — "ไม่วาดอะไรเลย" เป็นผลลัพธ์ที่
 * ปลอดภัยกว่าการวาดหมื่นเส้นจนหน้าค้าง และ DM จะเห็นทันทีว่าค่าที่ตั้งไว้ผิด (กริดหายไปเฉย ๆ)
 */
export function gridLinePositions(from: number, to: number, step: number, offset = 0): number[] {
  if (!Number.isFinite(step) || step <= 0 || !Number.isFinite(from) || !Number.isFinite(to)) return [];
  if (to < from) return [];

  // เส้นแรกที่ >= from · ใช้ ceil กับระยะจาก offset เพื่อให้ได้เส้นในกริดจริง ไม่ใช่เส้นที่ขอบภาพพอดี
  const firstIndex = Math.ceil((from - offset) / step);
  const lastIndex = Math.floor((to - offset) / step);
  if (lastIndex < firstIndex) return [];

  const count = lastIndex - firstIndex + 1;
  if (count > MAX_GRID_LINES_PER_AXIS) return [];

  const positions: number[] = [];
  for (let i = firstIndex; i <= lastIndex; i++) {
    positions.push(offset + i * step);
  }
  return positions;
}

/** กริดถี่เกินจนไม่ควรวาดไหม — ใช้บอกผู้ใช้ว่าทำไมกริดหาย แทนที่จะปล่อยให้งง */
export function isGridTooDense(width: number, height: number, step: number): boolean {
  if (!Number.isFinite(step) || step <= 0) return true;
  return width / step > MAX_GRID_LINES_PER_AXIS || height / step > MAX_GRID_LINES_PER_AXIS;
}

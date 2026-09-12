/**
 * Sprint H Part 1 — แกะ `scenes.grid` (jsonb ดิบจาก server) เป็นค่าที่ใช้วาดแผนที่ได้จริง
 *
 * `PATCH .../scenes/{id}/grid` (ฝั่ง C#, `SceneEndpoints.cs`) เขียนคอลัมน์นี้เป็น
 * `{"pixelsPerSquare": number, "offsetX": number, "offsetY": number}` — แต่ฉากที่ DM ยังไม่เคย
 * ตั้งกริดเลยจะมีค่าเป็น `'{}'` (ดีฟอลต์ของคอลัมน์) และเป็น jsonb อิสระที่ไม่มี schema บังคับ
 * ที่ชั้น DB จึงต้อง parse แบบไม่ไว้ใจข้อมูลเลย — พังแล้วต้องได้ดีฟอลต์ ไม่ใช่ throw
 *
 * `cellToPx`/`DEFAULT_PIXELS_PER_SQUARE` ใน `SceneMap.tsx` คือจุดแปลงหน่วยจุดเดียวของระบบ —
 * ไฟล์นี้แค่แกะตัวเลขออกมาให้ ไม่ตัดสินใจเรื่องการวาดเอง
 */

export interface SceneGrid {
  pixelsPerSquare: number;
  offsetX: number;
  offsetY: number;
}

/** ใช้ตอนแกะไม่สำเร็จ (grid เป็น `'{}'`, พัง, หรือฟิลด์หาย) — ต้องตรงกับ
 * `DEFAULT_PIXELS_PER_SQUARE` ใน `SceneMap.tsx` เพื่อไม่ให้พฤติกรรมเปลี่ยนตอนยังไม่ได้ตั้งกริด */
export const DEFAULT_SCENE_GRID: SceneGrid = { pixelsPerSquare: 50, offsetX: 0, offsetY: 0 };

/**
 * แกะ `scenes.grid` เป็น `SceneGrid` — คืนดีฟอลต์เสมอเมื่อ parse ไม่ได้หรือค่าที่ได้ใช้ไม่ได้จริง
 * (เช่น `pixelsPerSquare` เป็น 0/ติดลบ/ไม่ใช่ตัวเลข) ไม่เคย throw ให้ component ต้อง try/catch เอง
 *
 * ⚠️ `offsetX`/`offsetY` แกะออกมาเก็บไว้เฉยๆ ในรอบนี้ — **ยังไม่ได้ต่อเข้าการวาดจริง**
 * (`SceneMap` ยังไม่รับ offset เป็น prop) เขียนไว้ให้ชัดว่ายังไม่ทำ ไม่ใช่ลืม
 */
export function parseSceneGrid(gridJson: string | null | undefined): SceneGrid {
  if (!gridJson || gridJson === "{}") {
    return DEFAULT_SCENE_GRID;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(gridJson);
  } catch {
    return DEFAULT_SCENE_GRID;
  }

  if (typeof parsed !== "object" || parsed === null) {
    return DEFAULT_SCENE_GRID;
  }

  const raw = parsed as Record<string, unknown>;
  const pixelsPerSquare = raw.pixelsPerSquare;
  const offsetX = raw.offsetX;
  const offsetY = raw.offsetY;

  return {
    pixelsPerSquare:
      typeof pixelsPerSquare === "number" && Number.isFinite(pixelsPerSquare) && pixelsPerSquare > 0
        ? pixelsPerSquare
        : DEFAULT_SCENE_GRID.pixelsPerSquare,
    offsetX: typeof offsetX === "number" && Number.isFinite(offsetX) ? offsetX : DEFAULT_SCENE_GRID.offsetX,
    offsetY: typeof offsetY === "number" && Number.isFinite(offsetY) ? offsetY : DEFAULT_SCENE_GRID.offsetY,
  };
}

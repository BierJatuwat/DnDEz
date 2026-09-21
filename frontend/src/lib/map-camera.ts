/**
 * กล้องของแผนที่ (ซูม/เลื่อน) — ตรรกะล้วน ไม่มี React/DOM แยกมาให้เทสต์ได้ตรง ๆ ตามธรรมเนียมของโปรเจกต์
 *
 * **หน่วยคือ "พิกัดในภาพ" ไม่ใช่พิกเซลบนจอ** — ทั้งไฟล์นี้ทำงานในระบบพิกัดเดียวกับ `viewBox` ของ SVG
 * (ซึ่งก็คือพิกเซลของรูปแผนที่ต้นฉบับ) การแปลงจากพิกัดบนจอเป็นพิกัดในภาพทำที่ `SceneMap` ด้วย
 * `getScreenCTM()` ที่เดียว เหมือนที่ `pointToCell` ทำอยู่แล้ว — ห้ามคิดสูตรแปลงหน่วยเองที่นี่
 */

/** กรอบทั้งฉาก (ค่าคงที่ของฉากนั้น) — กล้องเลื่อนอยู่ข้างในนี้เท่านั้น */
export type MapView = { minX: number; minY: number; width: number; height: number };

/**
 * ตำแหน่งกล้อง · `x`/`y` คือระยะจากมุมซ้ายบนของฉาก (ไม่ใช่พิกัดสัมบูรณ์ — เก็บแบบนี้เพื่อให้ clamp
 * ที่ขอบเป็นเลข 0 ตรง ๆ อ่านง่ายกว่าการบวก minX ทุกครั้ง) · `scale` 1 = เห็นทั้งฉากพอดี
 */
export type MapCamera = { x: number; y: number; scale: number };

export const MIN_ZOOM = 1;

/** ซูมเข้าได้สูงสุด 8 เท่า — ที่กริด 50 พิกเซล/ช่อง ระดับนี้คือเห็นราว 2-3 ช่องเต็มจอ ซึ่งละเอียดเกินพอ
 *  สำหรับการวาง token · ปล่อยให้ซูมมากกว่านี้ได้ก็มีแต่หลง ไม่ได้ข้อมูลเพิ่ม (รูปแผนที่เบลอไปแล้ว) */
export const MAX_ZOOM = 8;

export const DEFAULT_CAMERA: MapCamera = { x: 0, y: 0, scale: 1 };

/** กรอบที่มองเห็นจริงตอนนี้ — ใส่ลง `viewBox` ได้ตรง ๆ */
export function visibleRect(camera: MapCamera, view: MapView) {
  const width = view.width / camera.scale;
  const height = view.height / camera.scale;
  return { x: view.minX + camera.x, y: view.minY + camera.y, width, height };
}

/**
 * ดึงกล้องกลับให้อยู่ในกรอบฉากเสมอ
 *
 * สองอย่างที่ต้องกันพร้อมกัน: **สเกลต่ำกว่า 1 ไม่ได้** (ซูมออกกว่า "เห็นทั้งฉาก" แล้วจะเหลือแต่พื้นที่ว่าง
 * รอบรูป ไม่มีข้อมูลอะไรเพิ่ม) และ **เลื่อนออกนอกฉากไม่ได้** — ถ้าไม่ clamp ผู้ใช้เลื่อนจนแผนที่หลุดจอแล้ว
 * หาทางกลับไม่เจอ ต้องรีเซ็ตอย่างเดียว
 */
export function clampCamera(camera: MapCamera, view: MapView): MapCamera {
  const scale = Math.min(Math.max(camera.scale, MIN_ZOOM), MAX_ZOOM);
  const maxX = view.width - view.width / scale;
  const maxY = view.height - view.height / scale;
  return {
    scale,
    x: Math.min(Math.max(camera.x, 0), Math.max(maxX, 0)),
    y: Math.min(Math.max(camera.y, 0), Math.max(maxY, 0)),
  };
}

/**
 * ซูมโดย **ตรึงจุดที่ชี้อยู่ให้อยู่กับที่** — ชี้ที่ประตูแล้วหมุนล้อ ประตูต้องไม่เลื่อนหนีไปไหน
 *
 * ถ้าซูมที่กึ่งกลางจออย่างเดียว การซูมเข้าไปดูมุมหนึ่งของแผนที่จะกลายเป็น "ซูมเข้า → เลื่อนตาม → ซูมเข้า"
 * ซ้ำ ๆ ซึ่งเป็นเหตุผลที่แอปแผนที่ทุกเจ้าตรึงจุดใต้เคอร์เซอร์
 *
 * @param anchor จุดที่ต้องการตรึง **ในพิกัดของฉาก** (แปลงจากพิกัดจอมาแล้ว)
 */
export function zoomAt(
  camera: MapCamera,
  view: MapView,
  factor: number,
  anchor: { x: number; y: number },
): MapCamera {
  const nextScale = Math.min(Math.max(camera.scale * factor, MIN_ZOOM), MAX_ZOOM);
  // สเกลชนเพดาน/พื้นแล้ว = ไม่เกิดการซูมจริง จึงต้องไม่เลื่อนกล้องด้วย (ไม่งั้นหมุนล้อต่อแล้วภาพไถลไปเรื่อย ๆ)
  if (nextScale === camera.scale) return camera;

  // ระยะจากขอบซ้ายของฉากถึง anchor ต้องคงสัดส่วนเดิมเมื่อเทียบกับกรอบที่มองเห็น
  const anchorX = anchor.x - view.minX;
  const anchorY = anchor.y - view.minY;
  const ratio = camera.scale / nextScale;

  return clampCamera(
    {
      scale: nextScale,
      x: anchorX - (anchorX - camera.x) * ratio,
      y: anchorY - (anchorY - camera.y) * ratio,
    },
    view,
  );
}

/** เลื่อนกล้องเป็นระยะในหน่วยพิกัดของฉาก (ค่าบวก = ภาพเลื่อนไปทางซ้าย/ขึ้น เหมือนลากกระดาษ) */
export function panBy(camera: MapCamera, view: MapView, dx: number, dy: number): MapCamera {
  return clampCamera({ ...camera, x: camera.x + dx, y: camera.y + dy }, view);
}

/**
 * แปลงการหมุนล้อเป็นตัวคูณสเกล
 *
 * `deltaY` ต่างกันมากระหว่างอุปกรณ์ (ล้อเมาส์ทีละ ~100, ทัชแพดทีละ 1-5) — ใช้ฐาน exponential กับค่าที่
 * คุมเพดานไว้ ทำให้ทั้งสองแบบรู้สึกใกล้เคียงกันและไม่มีทางกระโดดข้ามหลายระดับในทีเดียว
 */
export function wheelZoomFactor(deltaY: number): number {
  const clamped = Math.min(Math.max(deltaY, -100), 100);
  return Math.pow(1.0015, -clamped);
}

/** ระยะระหว่างสองนิ้ว — ใช้เทียบก่อน/หลังเพื่อหาอัตราซูมของ pinch */
export function pinchDistance(a: { x: number; y: number }, b: { x: number; y: number }): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

/** จุดกึ่งกลางระหว่างสองนิ้ว — จุดตรึงของ pinch */
export function midpoint(a: { x: number; y: number }, b: { x: number; y: number }) {
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

/**
 * ลากเกินระยะนี้ (พิกเซลบนจอ) = ตั้งใจเลื่อนแผนที่ ไม่ใช่คลิก
 *
 * ต้องมีเพราะการคลิกช่องว่างเพื่อวาง token กับการลากเพื่อเลื่อนแผนที่ใช้ปุ่มเดียวกัน — ถ้าไม่มีเกณฑ์นี้
 * มือที่สั่นนิดเดียวระหว่างคลิกจะกลายเป็นการเลื่อนแผนที่ และการเลื่อนทุกครั้งจะจบด้วยการวาง token
 * โดยไม่ตั้งใจ · 4 พิกเซลคือค่าที่เล็กพอจะไม่ขวางการคลิกปกติ แต่ใหญ่พอกันมือสั่น
 */
export const DRAG_PAN_THRESHOLD_PX = 4;

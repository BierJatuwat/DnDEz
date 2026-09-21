import { describe, it, expect } from "vitest";
import {
  DEFAULT_CAMERA,
  MAX_ZOOM,
  MIN_ZOOM,
  type MapView,
  clampCamera,
  midpoint,
  panBy,
  pinchDistance,
  visibleRect,
  wheelZoomFactor,
  zoomAt,
} from "./map-camera";

/** ฉากขนาด 1000×800 ที่มุมเริ่มไม่ใช่ (0,0) — กันสูตรที่บังเอิญถูกเพราะ minX/minY เป็นศูนย์ */
const view: MapView = { minX: 100, minY: 50, width: 1000, height: 800 };

describe("visibleRect", () => {
  it("สเกล 1 = เห็นทั้งฉากพอดี", () => {
    expect(visibleRect(DEFAULT_CAMERA, view)).toEqual({ x: 100, y: 50, width: 1000, height: 800 });
  });

  it("สเกล 2 = เห็นครึ่งเดียวทั้งกว้างและสูง", () => {
    expect(visibleRect({ x: 0, y: 0, scale: 2 }, view)).toEqual({ x: 100, y: 50, width: 500, height: 400 });
  });

  it("x/y ของกล้องนับจากมุมฉาก ไม่ใช่พิกัดสัมบูรณ์", () => {
    expect(visibleRect({ x: 200, y: 100, scale: 2 }, view)).toMatchObject({ x: 300, y: 150 });
  });
});

describe("clampCamera", () => {
  it("ซูมออกกว่า 1 ไม่ได้ (เห็นทั้งฉากคือสุดแล้ว)", () => {
    expect(clampCamera({ x: 0, y: 0, scale: 0.2 }, view).scale).toBe(MIN_ZOOM);
  });

  it("ซูมเข้าเกินเพดานไม่ได้", () => {
    expect(clampCamera({ x: 0, y: 0, scale: 999 }, view).scale).toBe(MAX_ZOOM);
  });

  it("เลื่อนออกนอกฉากไม่ได้ — ไม่งั้นแผนที่หลุดจอแล้วหาทางกลับไม่เจอ", () => {
    // สเกล 2 → เห็น 500×400 จึงเลื่อนได้มากสุด 500 ทางกว้าง และ 400 ทางสูง
    expect(clampCamera({ x: 9999, y: 9999, scale: 2 }, view)).toEqual({ x: 500, y: 400, scale: 2 });
    expect(clampCamera({ x: -50, y: -50, scale: 2 }, view)).toEqual({ x: 0, y: 0, scale: 2 });
  });

  it("ที่สเกล 1 เลื่อนไม่ได้เลย (ไม่มีอะไรให้เลื่อนไปดู)", () => {
    expect(clampCamera({ x: 300, y: 300, scale: 1 }, view)).toEqual({ x: 0, y: 0, scale: 1 });
  });
});

describe("zoomAt", () => {
  /** จุดที่ชี้อยู่ต้องอยู่ตำแหน่งเดิมบนจอหลังซูม = สัดส่วนของมันในกรอบที่เห็นต้องไม่เปลี่ยน */
  function anchorRatio(camera: { x: number; y: number; scale: number }, anchor: { x: number; y: number }) {
    const rect = visibleRect(camera, view);
    return { x: (anchor.x - rect.x) / rect.width, y: (anchor.y - rect.y) / rect.height };
  }

  it("ตรึงจุดที่ชี้ไว้กับที่ (ซูมเข้า)", () => {
    const anchor = { x: 600, y: 450 };
    const before = anchorRatio(DEFAULT_CAMERA, anchor);
    const after = anchorRatio(zoomAt(DEFAULT_CAMERA, view, 2, anchor), anchor);
    expect(after.x).toBeCloseTo(before.x, 6);
    expect(after.y).toBeCloseTo(before.y, 6);
  });

  it("ตรึงจุดที่ชี้ไว้กับที่ (ซูมออกจากที่ซูมเข้าอยู่)", () => {
    const start = { x: 400, y: 300, scale: 4 };
    const anchor = { x: 650, y: 480 };
    const before = anchorRatio(start, anchor);
    const after = anchorRatio(zoomAt(start, view, 0.5, anchor), anchor);
    expect(after.x).toBeCloseTo(before.x, 6);
    expect(after.y).toBeCloseTo(before.y, 6);
  });

  it("ซูมเข้าแล้วซูมออกด้วยตัวคูณกลับกัน ได้กล้องเดิม", () => {
    const anchor = { x: 700, y: 200 };
    const zoomed = zoomAt({ x: 100, y: 80, scale: 2 }, view, 2, anchor);
    const back = zoomAt(zoomed, view, 0.5, anchor);
    expect(back.scale).toBeCloseTo(2, 6);
    expect(back.x).toBeCloseTo(100, 6);
    expect(back.y).toBeCloseTo(80, 6);
  });

  it("ชนเพดานแล้วหมุนล้อต่อ ภาพต้องไม่ไถล", () => {
    const atMax = { x: 400, y: 300, scale: MAX_ZOOM };
    expect(zoomAt(atMax, view, 2, { x: 999, y: 999 })).toBe(atMax);
  });

  it("ชนพื้น (สเกล 1) แล้วซูมออกต่อ ภาพต้องไม่ไถล", () => {
    expect(zoomAt(DEFAULT_CAMERA, view, 0.5, { x: 999, y: 999 })).toBe(DEFAULT_CAMERA);
  });

  it("ผลลัพธ์อยู่ในกรอบฉากเสมอ แม้ anchor จะอยู่ริมสุด", () => {
    const camera = zoomAt(DEFAULT_CAMERA, view, 4, { x: view.minX, y: view.minY });
    expect(camera.x).toBeGreaterThanOrEqual(0);
    expect(camera.y).toBeGreaterThanOrEqual(0);
    const rect = visibleRect(camera, view);
    expect(rect.x + rect.width).toBeLessThanOrEqual(view.minX + view.width + 1e-6);
    expect(rect.y + rect.height).toBeLessThanOrEqual(view.minY + view.height + 1e-6);
  });
});

describe("panBy", () => {
  it("เลื่อนแล้วยังอยู่ในกรอบ", () => {
    expect(panBy({ x: 100, y: 100, scale: 2 }, view, 50, -30)).toEqual({ x: 150, y: 70, scale: 2 });
  });

  it("เลื่อนชนขอบแล้วหยุดที่ขอบ ไม่ทะลุ", () => {
    expect(panBy({ x: 400, y: 300, scale: 2 }, view, 9999, 9999)).toEqual({ x: 500, y: 400, scale: 2 });
  });
});

describe("wheelZoomFactor", () => {
  it("หมุนขึ้น (deltaY ลบ) = ซูมเข้า · หมุนลง = ซูมออก", () => {
    expect(wheelZoomFactor(-100)).toBeGreaterThan(1);
    expect(wheelZoomFactor(100)).toBeLessThan(1);
  });

  it("ไม่ขยับ = ไม่ซูม", () => {
    expect(wheelZoomFactor(0)).toBe(1);
  });

  it("ค่า delta มหาศาลจากบางอุปกรณ์ต้องไม่ทำให้กระโดดข้ามหลายระดับในทีเดียว", () => {
    // คุมไว้ที่ ±100 → ตัวคูณสูงสุดต่อหนึ่งครั้งอยู่ราว 1.16 เท่า
    expect(wheelZoomFactor(-100000)).toBeCloseTo(wheelZoomFactor(-100), 10);
    expect(wheelZoomFactor(-100000)).toBeLessThan(1.2);
    expect(wheelZoomFactor(100000)).toBeGreaterThan(0.8);
  });

  it("ทัชแพดที่ส่งค่าน้อย ๆ ก็ยังซูมได้ (ไม่ถูกปัดเป็น 1)", () => {
    expect(wheelZoomFactor(-3)).toBeGreaterThan(1);
  });
});

describe("pinch helpers", () => {
  it("ระยะสองนิ้ว", () => {
    expect(pinchDistance({ x: 0, y: 0 }, { x: 3, y: 4 })).toBe(5);
  });

  it("จุดกึ่งกลางสองนิ้ว", () => {
    expect(midpoint({ x: 0, y: 10 }, { x: 4, y: 20 })).toEqual({ x: 2, y: 15 });
  });
});

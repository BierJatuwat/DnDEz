import { describe, it, expect } from "vitest";
import { MAX_GRID_LINES_PER_AXIS, gridLinePositions, isGridTooDense } from "./map-grid";

describe("gridLinePositions", () => {
  it("เส้นทุกเส้นในช่วง รวมเส้นที่ตรงขอบพอดี", () => {
    expect(gridLinePositions(0, 200, 50)).toEqual([0, 50, 100, 150, 200]);
  });

  it("เริ่มจากเส้นแรกที่อยู่ในช่วง ไม่ใช่ขอบช่วง", () => {
    // ขอบเริ่มที่ 30 แต่กริดอยู่ที่ทวีคูณของ 50 → เส้นแรกคือ 50
    expect(gridLinePositions(30, 160, 50)).toEqual([50, 100, 150]);
  });

  it("offset เลื่อนทั้งกริด — ใช้ตอนตารางบนรูปไม่ได้เริ่มที่มุมซ้ายบนพอดี", () => {
    expect(gridLinePositions(0, 100, 50, 12)).toEqual([12, 62]);
  });

  it("offset ติดลบก็ยังได้เส้นที่ถูก", () => {
    expect(gridLinePositions(0, 100, 50, -8)).toEqual([42, 92]);
  });

  it("ช่วงที่เริ่มติดลบ (ฉากที่ไม่มีรูป กรอบขยายออกนอกศูนย์)", () => {
    expect(gridLinePositions(-120, 60, 50)).toEqual([-100, -50, 0, 50]);
  });

  it("ช่วงแคบจนไม่มีเส้นไหนอยู่ข้างใน = ลิสต์ว่าง ไม่ใช่พัง", () => {
    expect(gridLinePositions(51, 99, 50)).toEqual([]);
  });

  it("ช่วงกลับหัว (to < from) = ว่าง", () => {
    expect(gridLinePositions(200, 100, 50)).toEqual([]);
  });

  it("step ที่ใช้ไม่ได้ = ว่าง ไม่ใช่ลูปไม่รู้จบ", () => {
    expect(gridLinePositions(0, 100, 0)).toEqual([]);
    expect(gridLinePositions(0, 100, -50)).toEqual([]);
    expect(gridLinePositions(0, 100, Number.NaN)).toEqual([]);
    expect(gridLinePositions(0, 100, Number.POSITIVE_INFINITY)).toEqual([]);
  });

  it("ค่าขอบที่ไม่ใช่ตัวเลข = ว่าง", () => {
    expect(gridLinePositions(Number.NaN, 100, 50)).toEqual([]);
    expect(gridLinePositions(0, Number.NaN, 50)).toEqual([]);
  });

  it("ถี่เกินเพดาน = ไม่วาดเลย (กันหน้าค้างตอน DM พิมพ์ 'พิกเซลต่อช่อง' ผิดเป็นเลขน้อย ๆ)", () => {
    const tooMany = gridLinePositions(0, MAX_GRID_LINES_PER_AXIS * 10, 1);
    expect(tooMany).toEqual([]);
  });

  it("อยู่ที่เพดานพอดียังวาด", () => {
    const atLimit = gridLinePositions(0, (MAX_GRID_LINES_PER_AXIS - 1) * 10, 10);
    expect(atLimit).toHaveLength(MAX_GRID_LINES_PER_AXIS);
  });
});

describe("isGridTooDense", () => {
  it("กริดปกติไม่ถี่เกิน", () => {
    expect(isGridTooDense(2000, 1500, 50)).toBe(false);
  });

  it("ช่องเล็กมากบนแผนที่ใหญ่ = ถี่เกิน", () => {
    expect(isGridTooDense(4000, 3000, 5)).toBe(true);
  });

  it("step ใช้ไม่ได้ถือว่าถี่เกิน (จะได้ไม่พยายามวาด)", () => {
    expect(isGridTooDense(1000, 1000, 0)).toBe(true);
    expect(isGridTooDense(1000, 1000, -1)).toBe(true);
  });
});

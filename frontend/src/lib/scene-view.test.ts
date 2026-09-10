import { describe, it, expect } from "vitest";
import {
  healthRatio,
  isPointRevealed,
  partyHealth,
  sceneBounds,
  tokenInitial,
} from "./scene-view";
import type { CharacterSummary } from "./campaign-types";
import type { FogArea, SceneToken } from "./scene-types";

function character(over: Partial<CharacterSummary>): CharacterSummary {
  return {
    id: "c-1",
    name: "Hero",
    level: 1,
    ownerUserId: "u-1",
    controlledByDm: false,
    currentHp: 10,
    maxHp: 10,
    currentTempHp: 0,
    ...over,
  };
}

function token(over: Partial<SceneToken>): SceneToken {
  return {
    id: "t-1",
    entityType: "Character",
    entityId: "c-1",
    x: 0,
    y: 0,
    currentHp: null,
    maxHp: null,
    ...over,
  };
}

describe("partyHealth", () => {
  it("รวมเฉพาะตัวละครผู้เล่น ไม่รวม NPC ที่ DM คุม", () => {
    const result = partyHealth([
      character({ id: "a", currentHp: 12, maxHp: 20 }),
      character({ id: "b", currentHp: 30, maxHp: 30 }),
      character({ id: "npc", controlledByDm: true, currentHp: 59, maxHp: 59 }),
    ]);

    expect(result).toEqual({ current: 42, max: 50, memberCount: 2 });
  });

  it("ข้ามตัวที่ HP ถูกซ่อน (null) ทั้งตัว ไม่นับเป็น 0", () => {
    const result = partyHealth([
      character({ id: "a", currentHp: 12, maxHp: 20 }),
      character({ id: "hidden", currentHp: null, maxHp: null }),
    ]);

    // ถ้านับ null เป็น 0 max จะเป็น 20 เท่ากันแต่ memberCount จะเป็น 2 — ยืนยันว่าข้ามทั้งตัวจริง
    expect(result).toEqual({ current: 12, max: 20, memberCount: 1 });
  });

  it("ปาร์ตี้ว่างคืนศูนย์ ไม่ throw", () => {
    expect(partyHealth([])).toEqual({ current: 0, max: 0, memberCount: 0 });
  });
});

describe("isPointRevealed", () => {
  const areas: FogArea[] = [{ id: "a-1", x: 0, y: 0, radius: 10 }];

  it("จุดในวงถือว่าเห็น", () => {
    expect(isPointRevealed(5, 5, areas)).toBe(true);
  });

  it("จุดนอกวงถือว่ายังไม่เห็น", () => {
    expect(isPointRevealed(999, 999, areas)).toBe(false);
  });

  it("จุดที่ระยะเท่ากับรัศมีเป๊ะถือว่าเห็น — ต้องตรงกับฝั่ง server ที่ใช้ <=", () => {
    expect(isPointRevealed(10, 0, areas)).toBe(true);
  });

  it("ไม่มีพื้นที่เปิดเลย = ไม่เห็นอะไรเลย", () => {
    expect(isPointRevealed(0, 0, [])).toBe(false);
  });

  it("เห็นถ้าอยู่ในวงใดวงหนึ่ง (หลายวงรวมกัน)", () => {
    const many: FogArea[] = [
      { id: "a-1", x: 0, y: 0, radius: 5 },
      { id: "a-2", x: 100, y: 100, radius: 5 },
    ];
    expect(isPointRevealed(100, 100, many)).toBe(true);
  });
});

describe("sceneBounds", () => {
  it("ครอบทั้ง token และพื้นที่เปิด พร้อมเผื่อขอบ", () => {
    const bounds = sceneBounds(
      [token({ x: 50, y: 50 })],
      [{ id: "a-1", x: 0, y: 0, radius: 10 }],
      10,
    );

    // ซ้ายสุด = 0-10(รัศมี)-10(ขอบ) = -20 · ขวาสุด = 50+10(ขอบ) = 60 → กว้าง 80
    expect(bounds.minX).toBe(-20);
    expect(bounds.width).toBe(80);
  });

  it("scene ว่างคืนกรอบ fallback ไม่ใช่กว้าง 0 (ไม่งั้นแผนที่หายทั้งอัน)", () => {
    expect(sceneBounds([], [])).toEqual({ minX: 0, minY: 0, width: 100, height: 100 });
  });

  it("token ตัวเดียวไม่ทำให้กว้าง/สูงเป็น 0", () => {
    const bounds = sceneBounds([token({ x: 5, y: 5 })], [], 0);
    expect(bounds.width).toBeGreaterThan(0);
    expect(bounds.height).toBeGreaterThan(0);
  });
});

describe("tokenInitial", () => {
  it("ใช้ตัวแรกของชื่อเป็นตัวพิมพ์ใหญ่", () => {
    expect(tokenInitial("Aldric")).toBe("A");
  });

  it("ชื่อภาษาไทยใช้ตัวแรกได้เหมือนกัน", () => {
    expect(tokenInitial("อัลดริค")).toBe("อ");
  });

  it("ไม่มีชื่อคืนเครื่องหมายคำถาม ไม่ throw", () => {
    expect(tokenInitial(undefined)).toBe("?");
    expect(tokenInitial("   ")).toBe("?");
  });
});

describe("healthRatio", () => {
  it("คืนสัดส่วนปกติ", () => {
    expect(healthRatio(5, 10)).toBe(0.5);
  });

  it("คืน null เมื่อ HP ถูกซ่อน", () => {
    expect(healthRatio(null, 10)).toBeNull();
    expect(healthRatio(5, null)).toBeNull();
  });

  it("max เป็น 0 คืน null ไม่หารด้วยศูนย์", () => {
    expect(healthRatio(0, 0)).toBeNull();
  });

  it("ค่าเกินขอบถูกบีบให้อยู่ใน 0..1", () => {
    expect(healthRatio(-5, 10)).toBe(0);
    expect(healthRatio(15, 10)).toBe(1);
  });
});

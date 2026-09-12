import { describe, it, expect } from "vitest";
import { addStrokeCells, cellsAlongLine, chunkCells, legendItems } from "./terrain-paint";

/** ทุกคู่ติดกันต้องห่างกันไม่เกิน 1 ช่องทุกทิศ (Chebyshev) — ไม่มีรูโหว่ในเส้นที่ลาก */
function isContiguous(cells: { x: number; y: number }[]) {
  return cells.every(
    (c, i) => i === 0 || Math.max(Math.abs(c.x - cells[i - 1].x), Math.abs(c.y - cells[i - 1].y)) === 1,
  );
}

describe("cellsAlongLine — เติมช่องระหว่างจุดที่ pointermove สุ่มได้", () => {
  it("เส้นตรงแนวนอนได้ทุกช่องรวมหัวท้าย", () => {
    expect(cellsAlongLine({ x: 0, y: 0 }, { x: 3, y: 0 })).toEqual([
      { x: 0, y: 0 },
      { x: 1, y: 0 },
      { x: 2, y: 0 },
      { x: 3, y: 0 },
    ]);
  });

  it("ทแยงเดินทีละช่องทแยง", () => {
    expect(cellsAlongLine({ x: 0, y: 0 }, { x: 2, y: 2 })).toEqual([
      { x: 0, y: 0 },
      { x: 1, y: 1 },
      { x: 2, y: 2 },
    ]);
  });

  it("จุดเดียวกันได้ช่องเดียว", () => {
    expect(cellsAlongLine({ x: 4, y: 4 }, { x: 4, y: 4 })).toEqual([{ x: 4, y: 4 }]);
  });

  it("ลากถอยหลัง (ติดลบ) ก็ครบและเรียงจากต้นไปปลาย", () => {
    expect(cellsAlongLine({ x: 2, y: 1 }, { x: -1, y: 1 })).toEqual([
      { x: 2, y: 1 },
      { x: 1, y: 1 },
      { x: 0, y: 1 },
      { x: -1, y: 1 },
    ]);
  });

  /** เคสที่ลากเร็ว: pointermove กระโดดไกลในเหตุการณ์เดียว — ต้องไม่มีช่องโหว่ในกำแพง */
  it("เส้นเฉียงไม่ลงตัวยังต่อเนื่องไม่มีช่องโหว่ และจำนวนช่อง = ระยะ Chebyshev + 1", () => {
    const cells = cellsAlongLine({ x: 0, y: 0 }, { x: 5, y: 2 });

    expect(cells[0]).toEqual({ x: 0, y: 0 });
    expect(cells[cells.length - 1]).toEqual({ x: 5, y: 2 });
    expect(cells).toHaveLength(6);
    expect(isContiguous(cells)).toBe(true);
  });

  it("เส้นยาวทุกทิศต่อเนื่องเสมอ", () => {
    for (const to of [{ x: -7, y: 3 }, { x: 3, y: -9 }, { x: -4, y: -4 }, { x: 0, y: 11 }]) {
      const cells = cellsAlongLine({ x: 1, y: 1 }, to);
      expect(isContiguous(cells)).toBe(true);
      expect(cells[cells.length - 1]).toEqual(to);
    }
  });
});

describe("addStrokeCells — สะสมช่องระหว่างลาก", () => {
  it("ไม่เก็บช่องซ้ำ (ลากวนทับที่เดิม) และคงลำดับที่ลากผ่านครั้งแรก", () => {
    const stroke = addStrokeCells([{ x: 0, y: 0 }, { x: 1, y: 0 }], [{ x: 1, y: 0 }, { x: 2, y: 0 }, { x: 0, y: 0 }]);

    expect(stroke).toEqual([
      { x: 0, y: 0 },
      { x: 1, y: 0 },
      { x: 2, y: 0 },
    ]);
  });

  it("ไม่แก้ array เดิม (state ของ React ต้องไม่ถูกแก้ในที่)", () => {
    const original = [{ x: 0, y: 0 }];
    addStrokeCells(original, [{ x: 1, y: 1 }]);

    expect(original).toEqual([{ x: 0, y: 0 }]);
  });
});

describe("chunkCells — แบ่งส่งไม่เกินที่ server รับต่อครั้ง", () => {
  it("แบ่งเป็นก้อนละไม่เกินขนาดที่กำหนด ครบทุกช่อง", () => {
    const cells = Array.from({ length: 2_500 }, (_, i) => ({ x: i, y: 0 }));

    const chunks = chunkCells(cells, 1_000);

    expect(chunks.map((c) => c.length)).toEqual([1_000, 1_000, 500]);
    expect(chunks.flat()).toEqual(cells);
  });

  it("ว่างเปล่า = ไม่ต้องส่งอะไรเลย", () => {
    expect(chunkCells([], 1_000)).toEqual([]);
  });
});

describe("legendItems — คำอธิบายบนจอ ให้ผู้เล่นรู้เองโดยไม่ต้องมีใครบอก", () => {
  it("บอกเฉพาะชนิดที่มีอยู่จริงบนจอ พร้อมราคาการเดินเป็นภาษาคน", () => {
    const items = legendItems({ difficult: [{ x: 1, y: 1 }], blocked: [] });

    expect(items).toHaveLength(1);
    expect(items[0].kind).toBe("difficult");
    expect(items[0].text).toContain("10 ฟุต");
  });

  it("มีทั้งสองชนิด → สองบรรทัด กำแพงบอกว่าเดินผ่านไม่ได้", () => {
    const items = legendItems({ difficult: [{ x: 1, y: 1 }], blocked: [{ x: 2, y: 2 }] });

    expect(items.map((i) => i.kind)).toEqual(["difficult", "blocked"]);
    expect(items[1].text).toContain("ผ่านไม่ได้");
  });

  it("ไม่มีพื้นพิเศษบนจอ → ไม่ต้องมีคำอธิบาย (ไม่รกจอ)", () => {
    expect(legendItems({ difficult: [], blocked: [] })).toEqual([]);
  });
});

/**
 * Sprint H Part 6 — ตรรกะการวาดพื้นเดินยาก/กำแพงของ DM (ไม่มี React/fetch) แยกมาให้เทสต์ได้ตรง ๆ
 *
 * พิกัดทั้งหมดเป็น "ช่อง" เหมือน token — การแปลงช่อง↔พิกเซลมีที่ `SceneMap` ที่เดียวทั้งระบบ
 * (`cellToPx` / `pointToCell`) ไฟล์นี้ไม่รู้จักพิกเซลเลย
 */

export interface Cell {
  x: number;
  y: number;
}

/** พื้นของฉากตามที่ server ส่งมา — ผู้เล่นได้เฉพาะช่องในพื้นที่ที่เปิดหมอกแล้ว (server กรองให้) */
export interface SceneTerrain {
  difficult: Cell[];
  blocked: Cell[];
}

/** เครื่องมือของ DM — ชื่อตรงกับ `TerrainPaint` ฝั่ง API เป๊ะ (endpoint เทียบชื่อแบบ case-sensitive) */
export type TerrainPaintTool = "Difficult" | "Blocked" | "Clear";

/** ช่องสูงสุดต่อคำขอ — ต้องตรงกับ `SceneTerrainSerialization.MaxSquaresPerEdit` ฝั่ง API */
export const MAX_CELLS_PER_PAINT = 1_000;

/**
 * ทุกช่องบนเส้นจาก `from` ถึง `to` รวมหัวท้าย เรียงจากต้นไปปลาย ต่อเนื่องไม่มีช่องโหว่
 *
 * **ทำไมต้องมี**: `pointermove` สุ่มตำแหน่งตามจังหวะของเบราว์เซอร์ ไม่ใช่ทุกช่องที่เมาส์ผ่าน — ลากเร็ว ๆ
 * แล้วเก็บแค่ช่องที่ event รายงาน กำแพงจะมีรูให้ token เดินลอดได้ · เดินตามแกนที่ห่างกว่าทีละช่อง แล้ว
 * ปัดอีกแกนตามสัดส่วน จึงห่างกันไม่เกิน 1 ช่องทุกคู่ (Chebyshev — ตรงกับการเดินของ engine)
 */
export function cellsAlongLine(from: Cell, to: Cell): Cell[] {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const steps = Math.max(Math.abs(dx), Math.abs(dy));
  if (steps === 0) return [{ x: from.x, y: from.y }];

  const cells: Cell[] = [];
  for (let i = 0; i <= steps; i++) {
    cells.push({
      x: from.x + Math.round((dx * i) / steps),
      y: from.y + Math.round((dy * i) / steps),
    });
  }
  return cells;
}

/** สะสมช่องระหว่างลาก — ไม่เก็บซ้ำ คงลำดับที่ผ่านครั้งแรก และคืน array ใหม่ (ไม่แก้ state เดิมในที่) */
export function addStrokeCells(stroke: Cell[], added: Cell[]): Cell[] {
  const seen = new Set(stroke.map((c) => `${c.x},${c.y}`));
  const next = [...stroke];
  for (const cell of added) {
    const key = `${cell.x},${cell.y}`;
    if (!seen.has(key)) {
      seen.add(key);
      next.push(cell);
    }
  }
  return next;
}

/** แบ่งเป็นก้อนละไม่เกิน `size` ช่อง — ลากเส้นยาวเกินที่ server รับต่อครั้งจะส่งเป็นหลายคำขอ */
export function chunkCells(cells: Cell[], size: number): Cell[][] {
  const chunks: Cell[][] = [];
  for (let i = 0; i < cells.length; i += size) {
    chunks.push(cells.slice(i, i + size));
  }
  return chunks;
}

export interface LegendItem {
  kind: "difficult" | "blocked";
  text: string;
}

/**
 * คำอธิบายใต้แผนที่ — novice check ของ Part นี้: ผู้เล่นต้องรู้เองจากจอว่าตรงนั้นเดินยาก
 * โดยไม่ต้องมีใครบอก · บอกเฉพาะชนิดที่มีบนจอจริง (ไม่มีก็ไม่รกจอ)
 *
 * ราคา 10 ฟุตมาจาก SRD 5.2 "Difficult Terrain": ทุก 1 ฟุตในพื้นเดินยากเสีย 1 ฟุตเพิ่ม → ช่อง 5 ฟุตเสีย 10
 * (ตรงกับ `MovementCostResolver` ของ Part 8 ที่ HTTP test ของ Part 5 ยืนยันแล้ว)
 */
export function legendItems(terrain: SceneTerrain): LegendItem[] {
  const items: LegendItem[] = [];
  if (terrain.difficult.length > 0) {
    items.push({
      kind: "difficult",
      text: "ลายเฉียงสีน้ำตาล = พื้นเดินยาก (หนองน้ำ ซากหิน) — เดินช่องละ 10 ฟุต แทนปกติ 5 ฟุต",
    });
  }
  if (terrain.blocked.length > 0) {
    items.push({ kind: "blocked", text: "ช่องทึบสีเทา = กำแพง — เดินผ่านไม่ได้" });
  }
  return items;
}

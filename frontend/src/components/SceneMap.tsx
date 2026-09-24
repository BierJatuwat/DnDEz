"use client";

import { useEffect, useId, useMemo, useRef, useState } from "react";
import { healthRatio, isPointRevealed, sceneBounds, tokenInitial } from "@/lib/scene-view";
import { tokenLabel } from "@/lib/srd-view";
import type { FogArea, SceneToken } from "@/lib/scene-types";
import {
  addStrokeCells,
  cellsAlongLine,
  type Cell,
  type SceneTerrain,
  type TerrainPaintTool,
} from "@/lib/terrain-paint";
import {
  DEFAULT_CAMERA,
  DRAG_PAN_THRESHOLD_PX,
  MAX_ZOOM,
  MIN_ZOOM,
  type MapCamera,
  midpoint,
  panBy,
  pinchDistance,
  visibleRect,
  wheelZoomFactor,
  zoomAt,
} from "@/lib/map-camera";
import { gridLinePositions, isGridTooDense } from "@/lib/map-grid";

/** หนึ่งช่องบนกริดกินกี่พิกเซลตอนวาด — ใช้เมื่อฉากยังไม่ได้ตั้งกริดเอง */
const DEFAULT_PIXELS_PER_SQUARE = 50;

/** ลายนิ้วมือของกรอบฉาก — ใช้ดูว่าฉากเปลี่ยนไปจริงไหม (เทียบ object ตรง ๆ ไม่ได้ ตัวตนเปลี่ยนทุก render) */
const viewKeyOf = (v: { minX: number; minY: number; width: number; height: number }) =>
  `${v.minX},${v.minY},${v.width},${v.height}`;

/** ปุ่มซูมมุมขวาบนของแผนที่ — ทึบพอให้อ่านออกบนแผนที่ทุกสี */
const zoomButtonClass =
  "w-7 h-7 flex items-center justify-center rounded border border-white/25 bg-black/60 text-white " +
  "hover:bg-black/80 disabled:opacity-30 disabled:hover:bg-black/60";

/** สีของเส้นที่กำลังลาก (ตัวอย่างก่อนปล่อยเมาส์) ตามเครื่องมือ */
const PAINT_PREVIEW: Record<TerrainPaintTool, string> = {
  Difficult: "#d97706",
  Blocked: "#a8a29e",
  Clear: "#ef4444",
};

export interface ReachableCell {
  x: number;
  y: number;
  costFeet: number;
}

/**
 * Sprint G Part 6/10 — แผนที่ + token + หมอก + การลาก token · Sprint H Part 6 — พื้นเดินยาก/กำแพง + โหมดวาด
 *
 * **ตั้งใจให้โง่**: ไม่ fetch ไม่รู้จัก role ไม่ตัดสินใจว่าใครควรเห็นอะไร — วาดสิ่งที่ถูกส่งเข้ามาเท่านั้น
 * การกรองตาม role เกิดที่ server แล้ว (design doc §13.3) การให้ component นี้รู้จัก role ด้วยจะ
 * กลายเป็นจุดที่สองที่ตัดสินใจเรื่องเดียวกัน แล้ววันหนึ่งสองจุดจะไม่ตรงกัน · พื้นก็เช่นกัน: ผู้เล่นได้
 * `terrain` มาเฉพาะส่วนที่เปิดหมอกแล้ว (server กรอง) ไฟล์นี้วาดเท่าที่ได้มา
 *
 * ## หน่วยพิกัด — จุดแปลงหน่วยจุดเดียวของทั้งระบบ
 * **`x`/`y` ของ token หมอก และพื้นคือ "ช่อง" ไม่ใช่พิกเซล** (1 ช่อง = 5 ฟุตตาม SRD) — เก็บเป็นช่อง
 * เพราะกฎต้องเป๊ะ: ช่องเป็นจำนวนเต็ม คิดระยะแบบ Chebyshev แล้วได้คำตอบตายตัว ไม่มีปัดเศษ และตรงกับ
 * engine ฝั่ง server (Part 8/9/10) ที่คิดทุกอย่างเป็นช่อง
 *
 * ไฟล์นี้เป็น **ที่เดียวในระบบที่แปลงช่อง↔พิกเซล** (`cellToPx` ขาไป, `pointToCell` ขากลับ) — บทเรียนจาก
 * Owlbear Rodeo ที่ทุกคนต้องเรียก `grid.getDistance()` ตัวเดียวแทนการคิดเลขเอง · มุมซ้ายบนของช่อง
 * (ใช้วาดสี่เหลี่ยมเต็มช่อง) ก็ได้มาจาก `cellToPx` ไม่ได้คิดสูตรใหม่
 *
 * ## โหมดวาด (Sprint H Part 6)
 * ส่ง `paintTool` + `onPaint` มา = ลากทับช่องแล้วได้รายการช่องทั้งเส้นตอนปล่อยเมาส์ · ระหว่างนั้นลาก
 * token และวาง token ใหม่ไม่ได้ (คนละเจตนา ใช้ event เดียวกัน) · เส้นที่ `pointermove` สุ่มได้ห่าง ๆ
 * ถูกเติมช่องระหว่างทางด้วย `cellsAlongLine` ไม่งั้นลากเร็วแล้วกำแพงมีรูให้เดินลอด
 */
export function SceneMap({
  tokens,
  revealedAreas,
  imageUrl,
  nameOf,
  fogStyle,
  scale = "normal",
  emptyHint,
  pixelsPerSquare = DEFAULT_PIXELS_PER_SQUARE,
  gridOffsetX = 0,
  gridOffsetY = 0,
  showGrid: showGridDefault = true,
  reachable,
  draggableTokenIds,
  onTokenDrop,
  onEmptyCellClick,
  constrainToReachable = false,
  terrain,
  paintTool = null,
  onPaint,
}: {
  tokens: SceneToken[];
  revealedAreas: FogArea[];
  imageUrl: string | null;
  /** แปลง entityId → ชื่อที่แสดง (มาจากรายการตัวละครที่หน้านั้นมีอยู่แล้ว) */
  nameOf: (entityId: string) => string | undefined;
  fogStyle: "opaque" | "translucent";
  /** จอกลางอ่านจากระยะ 1-2 เมตร — ตัวอักษร/token ต้องใหญ่กว่าจอส่วนตัวชัดเจน */
  scale?: "normal" | "table";
  emptyHint?: string;
  /** ขนาดหนึ่งช่องเป็นพิกเซล — มาจาก `scenes.grid` ที่ DM ตั้งไว้ */
  pixelsPerSquare?: number;
  /**
   * จุดเริ่มของกริดบนรูป — มาจาก `scenes.grid.offsetX/offsetY` ที่ DM ตั้งไว้
   *
   * **เพิ่งต่อเข้าการวาดจริงตอนทำเส้นกริด (2026-09-22)** — ก่อนหน้านี้ `parseSceneGrid` แกะค่ามาแล้วทิ้งไว้เฉย ๆ
   * (เขียนกำกับไว้ใน `scene-grid.ts` ว่ายังไม่ได้ต่อ) ซึ่งแปลว่าช่อง "เลื่อน x/y" ในหน้าจัดกริดกดแล้วไม่เกิดอะไรขึ้น
   * · พอกริดมองเห็นได้แล้ว ค่านี้จำเป็นทันที: แผนที่ที่ตารางบนรูปไม่ได้เริ่มที่มุมซ้ายบนพอดีจะเห็นเส้นเหลื่อมชัดเจน
   */
  gridOffsetX?: number;
  gridOffsetY?: number;
  /** วาดเส้นกริดไหม — ปิดได้เมื่อรูปแผนที่มีตารางพิมพ์มาอยู่แล้ว (ซ้อนกันสองชั้นอ่านยากกว่าเดิม) */
  showGrid?: boolean;
  /** ช่องที่เดินไปได้จริง (server คำนวณตามกฎ) — ไฮไลต์ตอนลาก */
  reachable?: ReachableCell[];
  /**
   * token ที่ลากได้ — ระบุด้วย **token id ไม่ใช่ entity id** เพราะ entity เดียวมีได้หลาย token
   * บนแผนที่ (เช่นตัวละครที่ถูกวางซ้ำ) การผูกกับ entity จะกำกวมว่าหมายถึงวงไหน
   */
  draggableTokenIds?: string[];
  /** ปล่อย token ลงช่อง — ต้องบอกด้วยว่า token ไหน เพราะโหมดจัดฉากของ DM ลากได้หลายตัว */
  onTokenDrop?: (tokenId: string, cell: { x: number; y: number }) => void;
  /** คลิกช่องที่ยังไม่มี token — ใช้วาง token ใหม่ (โหมดจัดฉากของ DM) */
  onEmptyCellClick?: (cell: { x: number; y: number }) => void;
  /**
   * จำกัดปลายทางให้อยู่ใน `reachable` เท่านั้นไหม
   *
   * `true` = เดินตามกฎ (ผู้เล่น / NPC ในการต่อสู้) — วางนอกระยะไม่ได้ วงตัวอย่างขึ้นสีแดง
   * `false` = จัดฉาก (DM ย้ายอิสระ) — วางได้ทุกช่อง ไม่คิดระยะ ไม่เกิด OA (design doc §14.2)
   */
  constrainToReachable?: boolean;
  /** พื้นเดินยาก/กำแพง — ผู้เล่นได้เฉพาะส่วนที่เปิดหมอกแล้ว (server กรอง) · ไม่ส่ง = ไม่วาดชั้นพื้น */
  terrain?: SceneTerrain;
  /** เครื่องมือวาดของ DM — ไม่ใช่ null = อยู่ในโหมดวาด (ต้องส่ง `onPaint` มาด้วย) */
  paintTool?: TerrainPaintTool | null;
  /** ปล่อยเมาส์หลังลากวาด → ทุกช่องที่ลากผ่าน (เรียงตามลำดับที่ลาก ไม่ซ้ำ) */
  onPaint?: (cells: Cell[]) => void;
}) {
  const [measured, setMeasured] = useState<{ url: string; w: number; h: number } | null>(null);
  const [dragCell, setDragCell] = useState<{ x: number; y: number } | null>(null);
  const [dragTokenId, setDragTokenId] = useState<string | null>(null);
  const [camera, setCamera] = useState<MapCamera>(DEFAULT_CAMERA);
  /** prop `showGrid` เป็น **ค่าตั้งต้น** ส่วนปุ่มสลับเป็นของคนดูคนนั้น (จอกลางไม่มีปุ่มจึงคงค่าตั้งต้นตลอด) */
  const [gridVisible, setGridVisible] = useState(showGridDefault);
  /** นิ้ว/เมาส์ที่กดค้างอยู่บนแผนที่ — เก็บเพื่อรู้ว่ามีสองนิ้ว (pinch) ไหม และคำนวณระยะระหว่างนิ้ว */
  const pointers = useRef(new Map<number, { x: number; y: number }>());
  /** ระยะสองนิ้วครั้งก่อน — ใช้เทียบหาอัตราซูมของ pinch */
  const lastPinch = useRef<number | null>(null);
  /** จุดเริ่มลากเพื่อเลื่อนแผนที่ (null = ไม่ได้ลากเลื่อนอยู่) · `moved` แยกการลากออกจากการคลิก */
  const panStart = useRef<{ clientX: number; clientY: number; moved: boolean } | null>(null);
  /**
   * token ที่เพิ่งถูกกด — ต้องเป็น ref ไม่ใช่ state เพราะ `pointerdown` ของ token bubble ขึ้นไปถึง svg
   * **ในจังหวะเดียวกัน** ซึ่งตอนนั้น state ยังเป็นค่าเก่า · ถ้าอ่านจาก state ฝั่ง svg จะเข้าใจว่า "กดที่ว่าง"
   * แล้วเริ่มลากเลื่อนแผนที่พร้อมกับลาก token
   */
  const dragTokenRef = useRef<string | null>(null);
  const [stroke, setStroke] = useState<Cell[] | null>(null);
  // ref คือแหล่งจริงของเส้นที่กำลังลาก (state มีไว้วาดตัวอย่างเท่านั้น) — pointerup กับ pointerleave
  // ยิงติดกันได้หลังปล่อย pointer capture ถ้าอ่านจาก state (ยังเป็นค่าเก่าใน closure) เส้นเดียวจะถูกส่งซ้ำ
  const strokeRef = useRef<Cell[] | null>(null);
  const lastPaintCell = useRef<Cell | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  // id ของ pattern ต้องไม่ชนกันถ้ามีแผนที่หลายอันในหน้าเดียว · ตัด ":" ออก (useId คืน ":r0:") เผื่อ url(#...)
  const difficultPatternId = `difficult-${useId().replace(/:/g, "")}`;

  useEffect(() => {
    if (!imageUrl) return;

    let cancelled = false;
    const img = new window.Image();
    img.onload = () => {
      if (!cancelled) setMeasured({ url: imageUrl, w: img.naturalWidth, h: img.naturalHeight });
    };
    // โหลดรูปไม่ขึ้น (signed URL หมดอายุ/เน็ตหลุด) ไม่ใช่เหตุให้ทั้งแผนที่หายไป
    img.src = imageUrl;
    return () => {
      cancelled = true;
    };
  }, [imageUrl]);

  // memo ด้วยเหตุผลเดียวกับ `view` ด้านล่าง — ตัวตนต้องคงที่เมื่อค่าไม่เปลี่ยน ไม่งั้น memo ของ `view`
  // ที่พึ่งค่านี้ก็พังตามไปด้วย · เทียบ `measured.url` กับ `imageUrl` เพื่อไม่ใช้ขนาดของรูปเก่าตอนสลับแผนที่
  const imageSize = useMemo(
    () =>
      measured && imageUrl && measured.url === imageUrl
        ? { w: measured.w, h: measured.h }
        : null,
    [measured, imageUrl],
  );

  const pps = pixelsPerSquare > 0 ? pixelsPerSquare : DEFAULT_PIXELS_PER_SQUARE;
  /** ช่อง → พิกเซลของ "กึ่งกลางช่อง" (token ยืนกลางช่อง ไม่ใช่มุมช่อง) — คิด offset ของกริดด้วย */
  const cellToPx = (cell: number) => cell * pps + pps / 2;
  const cellToPxX = (cell: number) => cellToPx(cell) + gridOffsetX;
  const cellToPxY = (cell: number) => cellToPx(cell) + gridOffsetY;
  /** มุมซ้ายบนของช่อง — ใช้วาดสี่เหลี่ยมเต็มช่อง (พื้น/ขอบเขตเดิน) ได้จาก cellToPx ไม่คิดสูตรใหม่ */
  const cellCornerPxX = (cell: number) => cellToPxX(cell) - pps / 2;
  const cellCornerPxY = (cell: number) => cellToPxY(cell) - pps / 2;

  // ไม่มีรูป → ใช้กรอบจากเนื้อหา แล้วขยายเป็นพิกเซลด้วยสเกลเดียวกัน
  const contentCells = sceneBounds(tokens, revealedAreas);
  // memo เพื่อให้ **ตัวตนของ object คงที่เมื่อค่าไม่เปลี่ยน** — ไม่ใช่เรื่องความเร็ว แต่เพราะ `view`
  // เป็น dependency ของ effect ที่ผูก listener ล้อเมาส์ ถ้า object ใหม่ทุก render จะถอด/ใส่ listener
  // ใหม่ทุกครั้งที่อะไรก็ตามในหน้าขยับ
  const view = useMemo(
    () =>
      imageSize
        ? { minX: 0, minY: 0, width: imageSize.w, height: imageSize.h }
        : {
            minX: contentCells.minX * pps,
            minY: contentCells.minY * pps,
            width: Math.max(contentCells.width * pps, pps * 4),
            height: Math.max(contentCells.height * pps, pps * 4),
          },
    [
      imageSize,
      contentCells.minX,
      contentCells.minY,
      contentCells.width,
      contentCells.height,
      pps,
    ],
  );

  const showGrid = gridVisible;
  const isTable = scale === "table";
  const tokenRadius = pps * (isTable ? 0.42 : 0.38);
  const strokeWidth = Math.max(view.width, view.height) * 0.004;

  const paintMode = paintTool !== null && !!onPaint;
  const showEmptyHint = tokens.length === 0 && revealedAreas.length === 0;
  const draggableSet = new Set(draggableTokenIds ?? []);
  const canDrag = !paintMode && draggableSet.size > 0 && !!onTokenDrop;
  const reachableKeys = new Set((reachable ?? []).map((c) => `${c.x},${c.y}`));
  const occupiedKeys = new Set(tokens.map((t) => `${Math.round(t.x)},${Math.round(t.y)}`));

  /** พิกัดบนจอ → พิกัดในภาพ (หน่วยเดียวกับ `viewBox`) — จุดแปลงหน่วยจุดเดียว ทุกอย่างที่เหลือใช้ต่อจากนี้ */
  const pointToImage = (clientX: number, clientY: number) => {
    const ctm = svgRef.current?.getScreenCTM();
    if (!ctm) return null;
    return new DOMPoint(clientX, clientY).matrixTransform(ctm.inverse());
  };

  /** พิกัดบนจอ → ช่องบนกริด (ผ่าน CTM ของ SVG เพื่อให้ถูกต้องทุกขนาดจอ/ทุกการซูม) */
  const pointToCell = (clientX: number, clientY: number) => {
    const p = pointToImage(clientX, clientY);
    return p
      ? { x: Math.floor((p.x - gridOffsetX) / pps), y: Math.floor((p.y - gridOffsetY) / pps) }
      : null;
  };

  /** ระยะบนจอ → ระยะในภาพ — ใช้ตอนลากเลื่อนแผนที่ (ลากไป 10 พิกเซลบนจอ ภาพต้องเลื่อนตามเท่าที่ตาเห็น
   *  ไม่ว่าจะซูมอยู่ระดับไหน) · `a` ของ CTM คือสเกลแกน x ซึ่งเท่ากับแกน y เสมอในที่นี้ (ไม่มีการบิด) */
  const screenToImageScale = () => {
    const ctm = svgRef.current?.getScreenCTM();
    return ctm && ctm.a !== 0 ? 1 / ctm.a : null;
  };

  /** ส่งเส้นที่ลากครั้งเดียว — อ่าน/ล้าง ref ก่อน จึงเรียกซ้ำได้โดยไม่ส่งซ้ำ */
  const finishStroke = () => {
    const cells = strokeRef.current;
    strokeRef.current = null;
    lastPaintCell.current = null;
    setStroke(null);
    if (cells && cells.length > 0) onPaint?.(cells);
  };

  // โหมดจัดฉากวางได้ทุกช่อง — ข้อจำกัดระยะเป็นเรื่องของการเดินตามกฎเท่านั้น (§14.2)
  const dropIsLegal =
    dragCell !== null && (!constrainToReachable || reachableKeys.has(`${dragCell.x},${dragCell.y}`));

  const rect = visibleRect(camera, view);
  const zoomedIn = camera.scale > MIN_ZOOM;

  /** ซูมโดยตรึงจุดบนจอที่ระบุ — ใช้ร่วมกันทั้งล้อเมาส์และ pinch */
  const zoomAtClient = (factor: number, clientX: number, clientY: number) => {
    const anchor = pointToImage(clientX, clientY);
    if (!anchor) return;
    setCamera((current) => zoomAt(current, view, factor, anchor));
  };

  /** ซูมจากปุ่ม — ไม่มีเคอร์เซอร์ให้ตรึง จึงตรึงกึ่งกลางของกรอบที่เห็นอยู่ */
  const zoomFromButton = (factor: number) => {
    setCamera((current) => {
      const r = visibleRect(current, view);
      return zoomAt(current, view, factor, { x: r.x + r.width / 2, y: r.y + r.height / 2 });
    });
  };

  // ฉากเปลี่ยน (อัปโหลดแผนที่ใหม่/สลับ session) = กรอบอ้างอิงเปลี่ยนทั้งชุด → กล้องเดิมใช้ต่อไม่ได้
  // (ค่า x/y ที่เคยอยู่ในกรอบเก่าอาจหลุดนอกกรอบใหม่) รีเซ็ตกลับไปเห็นทั้งฉากเสมอ
  //
  // ปรับ state **ระหว่าง render** ไม่ใช่ใน effect — เป็นแพทเทิร์นที่ React แนะนำสำหรับ "รีเซ็ต state
  // เมื่อ prop เปลี่ยน" (ทำใน effect จะ render ด้วยกล้องเก่าหนึ่งเฟรมก่อน แล้วค่อยกระตุกกลับ และชน
  // กฎ `react-hooks/set-state-in-effect` ของ React 19.2 ตรง ๆ)
  const [cameraViewKey, setCameraViewKey] = useState(() => viewKeyOf(view));
  const viewKey = viewKeyOf(view);
  if (cameraViewKey !== viewKey) {
    setCameraViewKey(viewKey);
    setCamera(DEFAULT_CAMERA);
  }

  // ล้อเมาส์ต้องผูกแบบ native — React ผูก `wheel` เป็น passive listener ที่ root ทำให้ preventDefault
  // ไม่มีผล แล้วหน้าเว็บจะเลื่อนตามไปด้วยทุกครั้งที่ซูม
  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;

    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const ctm = svg.getScreenCTM();
      if (!ctm) return;
      const anchor = new DOMPoint(e.clientX, e.clientY).matrixTransform(ctm.inverse());
      setCamera((current) => zoomAt(current, view, wheelZoomFactor(e.deltaY), anchor));
    };

    svg.addEventListener("wheel", onWheel, { passive: false });
    return () => svg.removeEventListener("wheel", onWheel);
  }, [view]);

  return (
    <div className="relative w-full overflow-hidden rounded border border-black/10 dark:border-white/10 bg-neutral-950">
      <svg
        ref={svgRef}
        viewBox={`${rect.x} ${rect.y} ${rect.width} ${rect.height}`}
        className={isTable ? "w-full aspect-[16/10]" : "w-full aspect-[4/3]"}
        // `touchAction: none` ตลอดเวลา ไม่ใช่เฉพาะโหมดวาด — ถ้าไม่ปิด เบราว์เซอร์จะกินการลากนิ้วไปเลื่อนหน้าเว็บ
        // ทำให้ทั้ง pinch ซูม ลากเลื่อนแผนที่ และลาก token บนจอสัมผัสใช้ไม่ได้เลย
        style={{
          touchAction: "none",
          cursor: paintMode ? "crosshair" : zoomedIn ? "grab" : undefined,
        }}
        role="img"
        aria-label="แผนที่ฉาก"
        onPointerDown={(e) => {
          pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });

          if (paintMode) {
            const cell = pointToCell(e.clientX, e.clientY);
            if (!cell) return;
            // จับ pointer ไว้ — ลากออกนอกแผนที่ชั่วคราวแล้วกลับเข้ามา เส้นยังต่อเนื่อง
            e.currentTarget.setPointerCapture?.(e.pointerId);
            lastPaintCell.current = cell;
            strokeRef.current = [cell];
            setStroke([cell]);
            return;
          }

          // นิ้วที่สอง = เริ่ม pinch → ยกเลิกทุกอย่างที่นิ้วแรกกำลังทำอยู่ (ลาก token / ลากเลื่อน)
          // ไม่งั้นพอปล่อยนิ้ว จะกลายเป็นการวาง token ที่ไหนก็ไม่รู้ตามจุดที่นิ้วอยู่ตอนนั้น
          if (pointers.current.size === 2) {
            const [a, b] = [...pointers.current.values()];
            lastPinch.current = pinchDistance(a, b);
            panStart.current = null;
            dragTokenRef.current = null;
            setDragCell(null);
            setDragTokenId(null);
            return;
          }

          // กดบนที่ว่าง (ไม่ได้จับ token) = เตรียมลากเลื่อนแผนที่ · ยังไม่รู้ว่าเป็นคลิกหรือลาก
          // จนกว่าจะขยับเกินเกณฑ์ — ตัดสินตอน pointerup
          if (dragTokenRef.current === null) {
            e.currentTarget.setPointerCapture?.(e.pointerId);
            panStart.current = { clientX: e.clientX, clientY: e.clientY, moved: false };
          }
        }}
        onPointerMove={(e) => {
          if (pointers.current.has(e.pointerId)) {
            pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
          }

          // pinch มาก่อนเสมอ — สองนิ้วบนจอไม่ใช่การวาดหรือการลากไม่ว่าจะอยู่โหมดไหน
          if (pointers.current.size === 2) {
            const [a, b] = [...pointers.current.values()];
            const distance = pinchDistance(a, b);
            const previous = lastPinch.current;
            lastPinch.current = distance;
            if (previous && previous > 0 && distance > 0) {
              const center = midpoint(a, b);
              zoomAtClient(distance / previous, center.x, center.y);
            }
            return;
          }

          if (paintMode) {
            const last = lastPaintCell.current;
            if (strokeRef.current === null || last === null) return;
            const cell = pointToCell(e.clientX, e.clientY);
            if (!cell || (cell.x === last.x && cell.y === last.y)) return;
            lastPaintCell.current = cell;
            strokeRef.current = addStrokeCells(strokeRef.current, cellsAlongLine(last, cell));
            setStroke(strokeRef.current);
            return;
          }

          if (dragCell !== null) {
            const cell = pointToCell(e.clientX, e.clientY);
            if (cell) setDragCell(cell);
            return;
          }

          // ลากเลื่อนแผนที่ — เลื่อนเท่าที่ตาเห็น (แปลงระยะบนจอเป็นระยะในภาพตามสเกลปัจจุบัน)
          const start = panStart.current;
          if (!start) return;
          const dxScreen = e.clientX - start.clientX;
          const dyScreen = e.clientY - start.clientY;
          if (!start.moved && Math.hypot(dxScreen, dyScreen) < DRAG_PAN_THRESHOLD_PX) return;
          const toImage = screenToImageScale();
          if (toImage === null) return;
          start.moved = true;
          start.clientX = e.clientX;
          start.clientY = e.clientY;
          setCamera((current) => panBy(current, view, -dxScreen * toImage, -dyScreen * toImage));
        }}
        onPointerUp={(e) => {
          pointers.current.delete(e.pointerId);
          if (pointers.current.size < 2) lastPinch.current = null;

          if (paintMode) {
            finishStroke();
            return;
          }

          const panned = panStart.current?.moved ?? false;
          panStart.current = null;

          if (dragCell !== null && dragTokenId !== null) {
            if (dropIsLegal) onTokenDrop?.(dragTokenId, dragCell);
          } else if (onEmptyCellClick && !panned) {
            // ไม่ได้ลากอะไรอยู่ = คลิกเปล่าบนแผนที่ → ใช้วาง token ใหม่ (เฉพาะช่องที่ยังว่าง
            // เพื่อไม่ให้วางทับตัวที่มีอยู่แล้วโดยไม่ตั้งใจ) · ลากเลื่อนแผนที่แล้วต้องไม่นับเป็นคลิก
            const cell = pointToCell(e.clientX, e.clientY);
            if (cell && !occupiedKeys.has(`${cell.x},${cell.y}`)) onEmptyCellClick(cell);
          }
          dragTokenRef.current = null;
          setDragCell(null);
          setDragTokenId(null);
        }}
        onPointerCancel={(e) => {
          pointers.current.delete(e.pointerId);
          lastPinch.current = null;
          panStart.current = null;
          dragTokenRef.current = null;
        }}
        onPointerLeave={() => {
          if (paintMode) {
            finishStroke();
            return;
          }
          panStart.current = null;
          dragTokenRef.current = null;
          setDragCell(null);
          setDragTokenId(null);
        }}
      >
        <defs>
          {/* หมอก = สี่เหลี่ยมทึบทั้งผืน แล้ว "เจาะรู" ตรงพื้นที่ที่เปิดแล้ว */}
          <mask id="fog-holes">
            <rect x={view.minX} y={view.minY} width={view.width} height={view.height} fill="white" />
            {revealedAreas.map((a) => (
              <circle
                key={a.id}
                cx={cellToPxX(a.x)}
                cy={cellToPxY(a.y)}
                r={a.radius * pps}
                fill="black"
              />
            ))}
          </mask>
          {/* ลายเฉียงของพื้นเดินยาก — ลายต้องต่างจากกำแพงชัด ๆ ทั้งสีและพื้นผิว (คนตาบอดสีแยกด้วยลายได้) */}
          <pattern
            id={difficultPatternId}
            patternUnits="userSpaceOnUse"
            width={pps / 4}
            height={pps / 4}
            patternTransform="rotate(45)"
          >
            <rect width={pps / 4} height={pps / 4} fill="#78350f" fillOpacity={0.4} />
            <line x1={0} y1={0} x2={0} y2={pps / 4} stroke="#f59e0b" strokeWidth={pps / 14} strokeOpacity={0.85} />
          </pattern>
        </defs>

        {imageUrl && (
          <image
            href={imageUrl}
            x={0}
            y={0}
            width={imageSize?.w ?? view.width}
            height={imageSize?.h ?? view.height}
            preserveAspectRatio="xMidYMid slice"
          />
        )}

        {/* เส้นกริด — อยู่เหนือรูปแต่ใต้ทุกอย่างที่เหลือ: มันคือ "กระดาษตาราง" ที่ของอื่นวางทับ ไม่ใช่ของที่ต้องเด่น
            · `vectorEffect="non-scaling-stroke"` ทำให้เส้นบางเท่าเดิมบนจอทุกระดับซูม (ถ้าไม่ใส่ ซูมเข้า 8 เท่า
            แล้วเส้นจะหนาเป็นแถบทึบจนบังแผนที่) · ไม่รับ pointer event เด็ดขาด ไม่งั้นบังการคลิกวาง token
            · วาดสองรอบ (ขาวจาง ๆ กว้างกว่าเป็นรัศมี แล้วค่อยทับด้วยดำแคบกว่า) แทนสีเดียว — เส้นสีเดียว
            อ่านไม่ออกบนแผนที่บางแบบเสมอ (ขาวล้วนจมกับกระดาษ/parchment สว่าง, ดำล้วนจมกับแผนที่มืด)
            คู่ขาว-ดำนี้อ่านออกได้ทั้งสองแบบ */}
        {showGrid && (() => {
          const xs = gridLinePositions(view.minX, view.minX + view.width, pps, gridOffsetX);
          const ys = gridLinePositions(view.minY, view.minY + view.height, pps, gridOffsetY);
          return (
            <>
              {/* ขาวบาง ๆ วางก่อนเป็น "รัศมี" ให้เส้นดำด้านบนอ่านออกแม้แผนที่พื้นเข้ม — สลับกันไม่พอ
                  เพราะขาวล้วนจางเกินไปบนแผนที่พื้นสว่าง (กระดาษ/parchment) แบบในภาพตัวอย่างจริง
                  · ความทึบเดิม (ขาว .5 / ดำ .45) ยังจางบนพื้นหินสีเทากลาง — เทียบภาพจริงบน 4 พื้นแล้วค่านี้อ่านออกทุกแบบ */}
              <g stroke="#ffffff" strokeOpacity={0.65} strokeWidth={3} style={{ pointerEvents: "none" }}>
                {xs.map((x) => (
                  <line key={`gx-halo-${x}`} x1={x} y1={view.minY} x2={x} y2={view.minY + view.height} vectorEffect="non-scaling-stroke" />
                ))}
                {ys.map((y) => (
                  <line key={`gy-halo-${y}`} x1={view.minX} y1={y} x2={view.minX + view.width} y2={y} vectorEffect="non-scaling-stroke" />
                ))}
              </g>
              <g stroke="#000000" strokeOpacity={0.7} strokeWidth={1.25} style={{ pointerEvents: "none" }}>
                {xs.map((x) => (
                  <line key={`gx-${x}`} x1={x} y1={view.minY} x2={x} y2={view.minY + view.height} vectorEffect="non-scaling-stroke" />
                ))}
                {ys.map((y) => (
                  <line key={`gy-${y}`} x1={view.minX} y1={y} x2={view.minX + view.width} y2={y} vectorEffect="non-scaling-stroke" />
                ))}
              </g>
            </>
          );
        })()}

        {/* พื้นอยู่ใต้หมอก — ของ DM (หมอกโปร่ง) ที่อยู่ในพื้นที่ปิดจะดูจางลงเหมือน token ในหมอก */}
        {terrain && (
          <g>
            {terrain.difficult.map((c) => (
              <rect
                key={`difficult-${c.x},${c.y}`}
                x={cellCornerPxX(c.x)}
                y={cellCornerPxY(c.y)}
                width={pps}
                height={pps}
                fill={`url(#${difficultPatternId})`}
                stroke="#b45309"
                strokeOpacity={0.7}
                strokeWidth={strokeWidth * 0.5}
              >
                <title>พื้นเดินยาก — เดินช่องละ 10 ฟุต</title>
              </rect>
            ))}
            {terrain.blocked.map((c) => (
              <rect
                key={`blocked-${c.x},${c.y}`}
                x={cellCornerPxX(c.x)}
                y={cellCornerPxY(c.y)}
                width={pps}
                height={pps}
                fill="#57534e"
                fillOpacity={0.92}
                stroke="#1c1917"
                strokeWidth={strokeWidth * 0.6}
              >
                <title>กำแพง — เดินผ่านไม่ได้</title>
              </rect>
            ))}
          </g>
        )}

        <rect
          x={view.minX}
          y={view.minY}
          width={view.width}
          height={view.height}
          fill="#0a0a0a"
          fillOpacity={fogStyle === "opaque" ? 1 : 0.55}
          mask="url(#fog-holes)"
        />

        {/* ขอบเขตที่เดินไปได้ — โผล่เฉพาะตอนกำลังลาก เพื่อไม่ให้แผนที่รกตลอดเวลา
            §3.1: นี่คือการกระจายตามต้นทุนจริง ไม่ใช่วงรัศมี ช่องที่ไกลเท่ากันอาจไปถึงได้ไม่เท่ากัน */}
        {dragCell !== null &&
          (reachable ?? []).map((c) => (
            <rect
              key={`reach-${c.x},${c.y}`}
              x={cellCornerPxX(c.x)}
              y={cellCornerPxY(c.y)}
              width={pps}
              height={pps}
              fill="#22c55e"
              fillOpacity={0.14}
              stroke="#22c55e"
              strokeOpacity={0.3}
              strokeWidth={strokeWidth * 0.5}
            />
          ))}

        {revealedAreas.map((a) => (
          <circle
            key={`edge-${a.id}`}
            cx={cellToPxX(a.x)}
            cy={cellToPxY(a.y)}
            r={a.radius * pps}
            fill="none"
            stroke="#22c55e"
            strokeOpacity={0.35}
            strokeWidth={strokeWidth}
          />
        ))}

        {tokens.map((t) => {
          const name = tokenLabel(t, nameOf);
          const ratio = healthRatio(t.currentHp, t.maxHp);
          const hiddenFromPlayers =
            fogStyle === "translucent" && !isPointRevealed(t.x, t.y, revealedAreas);
          const isMonster = t.entityType === "Monster";
          const isDraggable = canDrag && draggableSet.has(t.id);
          const cx = cellToPxX(t.x);
          const cy = cellToPxY(t.y);

          return (
            <g key={t.id}>
              {/* บน token เขียนได้แค่ตัวอักษรแรก (วงกลมเล็ก) — ชื่อเต็มอยู่ใน title ให้ชี้ค้างแล้วอ่านได้
                  มีประโยชน์ขึ้นมากตั้งแต่ token ของมอนสเตอร์มีชื่อจริง ไม่ใช่ "?" เหมือนเดิม */}
              <title>{name}</title>
              <circle
                cx={cx}
                cy={cy}
                r={tokenRadius}
                fill={isMonster ? "#7f1d1d" : "#1e3a8a"}
                stroke={hiddenFromPlayers ? "#a3a3a3" : isDraggable ? "#facc15" : "#22c55e"}
                strokeOpacity={hiddenFromPlayers ? 0.7 : 1}
                strokeDasharray={hiddenFromPlayers ? `${strokeWidth * 2}` : undefined}
                strokeWidth={strokeWidth * 1.5}
                style={isDraggable ? { cursor: "grab" } : undefined}
                onPointerDown={
                  isDraggable
                    ? (e) => {
                        e.currentTarget.releasePointerCapture?.(e.pointerId);
                        dragTokenRef.current = t.id;
                        setDragCell({ x: Math.round(t.x), y: Math.round(t.y) });
                        setDragTokenId(t.id);
                      }
                    : undefined
                }
              />
              <text
                x={cx}
                y={cy + tokenRadius * 0.35}
                textAnchor="middle"
                fontSize={tokenRadius * 1.1}
                fill="#f5f5f5"
                fontWeight={600}
                style={{ pointerEvents: "none" }}
              >
                {tokenInitial(name)}
              </text>

              {/* แถบพลังชีวิตใต้ token — แสดงเฉพาะตัวที่ server ส่ง HP มาให้จริง (null = ตั้งใจไม่บอก) */}
              {ratio !== null && (
                <>
                  <rect
                    x={cx - tokenRadius}
                    y={cy + tokenRadius * 1.3}
                    width={tokenRadius * 2}
                    height={strokeWidth * 2.5}
                    fill="#404040"
                    rx={strokeWidth}
                  />
                  <rect
                    x={cx - tokenRadius}
                    y={cy + tokenRadius * 1.3}
                    width={tokenRadius * 2 * ratio}
                    height={strokeWidth * 2.5}
                    fill={ratio > 0.5 ? "#22c55e" : ratio > 0.25 ? "#eab308" : "#ef4444"}
                    rx={strokeWidth}
                  />
                </>
              )}
            </g>
          );
        })}

        {/* เส้นที่กำลังลากวาด — เห็นก่อนปล่อยเมาส์ว่าจะระบายช่องไหนบ้าง */}
        {stroke !== null &&
          paintTool !== null &&
          stroke.map((c) => (
            <rect
              key={`stroke-${c.x},${c.y}`}
              x={cellCornerPxX(c.x)}
              y={cellCornerPxY(c.y)}
              width={pps}
              height={pps}
              fill={PAINT_PREVIEW[paintTool]}
              fillOpacity={0.35}
              stroke={PAINT_PREVIEW[paintTool]}
              strokeWidth={strokeWidth}
              style={{ pointerEvents: "none" }}
            />
          ))}

        {/* ตัวอย่างตำแหน่งที่จะไปตก — เขียวคือวางได้ แดงคือเกินระยะ (บอกก่อนปล่อยมือ) */}
        {dragCell !== null && (
          <circle
            cx={cellToPxX(dragCell.x)}
            cy={cellToPxY(dragCell.y)}
            r={tokenRadius}
            fill={dropIsLegal ? "#22c55e" : "#ef4444"}
            fillOpacity={0.35}
            stroke={dropIsLegal ? "#22c55e" : "#ef4444"}
            strokeWidth={strokeWidth * 1.5}
            style={{ pointerEvents: "none" }}
          />
        )}
      </svg>

      {/* กริดถี่เกินจนไม่วาด — ต้องบอกว่าทำไมหาย ไม่งั้น DM ที่พิมพ์ "พิกเซลต่อช่อง" ผิดจะเห็นกริดหายเฉย ๆ
          แล้วไม่รู้ว่าต้องไปแก้ตรงไหน */}
      {showGrid && isGridTooDense(view.width, view.height, pps) && (
        <p className="absolute bottom-2 left-2 rounded bg-black/70 px-2 py-1 text-[11px] text-white">
          ช่องเล็กเกินจนวาดตารางไม่ไหว — ตรวจค่า &quot;พิกเซล/ช่อง&quot; ในหัวข้อจัดกริด
        </p>
      )}

      {showEmptyHint && emptyHint && (
        <p className="absolute inset-0 flex items-center justify-center px-6 text-center text-sm opacity-60">
          {emptyHint}
        </p>
      )}

      {/* ปุ่มซูม — ล้อเมาส์กับ pinch เป็นทางหลัก แต่ต้องมีปุ่มด้วยเพราะ (ก) ไม่มีอะไรบอกผู้ใช้ว่าซูมได้
          (ข) ทัชแพดบางรุ่น/เมาส์ไม่มีล้อ (ค) คีย์บอร์ดล้วนเข้าถึงได้ · จอกลาง (`table`) ไม่มีปุ่ม
          ตาม §13.1 ที่ระบุว่าช่อง Input ของจอกลางคือ "ไม่มีเลย" */}
      {!isTable && (
        <div className="absolute right-2 top-2 flex flex-col gap-1">
          <button
            type="button"
            onClick={() => zoomFromButton(1.4)}
            disabled={camera.scale >= MAX_ZOOM}
            aria-label="ซูมเข้า"
            className={zoomButtonClass}
          >
            +
          </button>
          <button
            type="button"
            onClick={() => zoomFromButton(1 / 1.4)}
            disabled={camera.scale <= MIN_ZOOM}
            aria-label="ซูมออก"
            className={zoomButtonClass}
          >
            −
          </button>
          {/* สลับกริด — แผนที่หลายใบมีตารางพิมพ์มาในรูปอยู่แล้ว ซ้อนอีกชั้นทำให้อ่านยากกว่าเดิม
              (สถานะอยู่ที่ตัว component เพราะเป็นความชอบของคนดูแต่ละคน ไม่ใช่ค่าของฉากที่ต้องตรงกันทุกจอ) */}
          <button
            type="button"
            onClick={() => setGridVisible((v) => !v)}
            aria-pressed={showGrid}
            aria-label={showGrid ? "ซ่อนเส้นตาราง" : "แสดงเส้นตาราง"}
            title={showGrid ? "ซ่อนเส้นตาราง" : "แสดงเส้นตาราง"}
            className={`${zoomButtonClass} ${showGrid ? "" : "opacity-50"}`}
          >
            #
          </button>
          {zoomedIn && (
            <button
              type="button"
              onClick={() => setCamera(DEFAULT_CAMERA)}
              aria-label="ดูทั้งแผนที่"
              title={`ซูม ${camera.scale.toFixed(1)}× — กดเพื่อดูทั้งแผนที่`}
              className={`${zoomButtonClass} text-[10px] tabular-nums`}
            >
              {camera.scale.toFixed(1)}×
            </button>
          )}
        </div>
      )}
    </div>
  );
}

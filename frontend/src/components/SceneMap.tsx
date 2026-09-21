"use client";

import { useEffect, useId, useRef, useState } from "react";
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

/** หนึ่งช่องบนกริดกินกี่พิกเซลตอนวาด — ใช้เมื่อฉากยังไม่ได้ตั้งกริดเอง */
const DEFAULT_PIXELS_PER_SQUARE = 50;

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

  const imageSize =
    measured && imageUrl && measured.url === imageUrl
      ? { w: measured.w, h: measured.h }
      : null;

  const pps = pixelsPerSquare > 0 ? pixelsPerSquare : DEFAULT_PIXELS_PER_SQUARE;
  /** ช่อง → พิกเซลของ "กึ่งกลางช่อง" (token ยืนกลางช่อง ไม่ใช่มุมช่อง) */
  const cellToPx = (cell: number) => cell * pps + pps / 2;
  /** มุมซ้ายบนของช่อง — ใช้วาดสี่เหลี่ยมเต็มช่อง (พื้น/ขอบเขตเดิน) ได้จาก cellToPx ไม่คิดสูตรใหม่ */
  const cellCornerPx = (cell: number) => cellToPx(cell) - pps / 2;

  // ไม่มีรูป → ใช้กรอบจากเนื้อหา แล้วขยายเป็นพิกเซลด้วยสเกลเดียวกัน
  const contentCells = sceneBounds(tokens, revealedAreas);
  const view = imageSize
    ? { minX: 0, minY: 0, width: imageSize.w, height: imageSize.h }
    : {
        minX: contentCells.minX * pps,
        minY: contentCells.minY * pps,
        width: Math.max(contentCells.width * pps, pps * 4),
        height: Math.max(contentCells.height * pps, pps * 4),
      };

  const isTable = scale === "table";
  const tokenRadius = pps * (isTable ? 0.42 : 0.38);
  const strokeWidth = Math.max(view.width, view.height) * 0.004;

  const paintMode = paintTool !== null && !!onPaint;
  const showEmptyHint = tokens.length === 0 && revealedAreas.length === 0;
  const draggableSet = new Set(draggableTokenIds ?? []);
  const canDrag = !paintMode && draggableSet.size > 0 && !!onTokenDrop;
  const reachableKeys = new Set((reachable ?? []).map((c) => `${c.x},${c.y}`));
  const occupiedKeys = new Set(tokens.map((t) => `${Math.round(t.x)},${Math.round(t.y)}`));

  /** พิกัดบนจอ → ช่องบนกริด (ผ่าน CTM ของ SVG เพื่อให้ถูกต้องทุกขนาดจอ/ทุกการซูม) */
  const pointToCell = (clientX: number, clientY: number) => {
    const svg = svgRef.current;
    const ctm = svg?.getScreenCTM();
    if (!svg || !ctm) return null;
    const p = new DOMPoint(clientX, clientY).matrixTransform(ctm.inverse());
    return { x: Math.floor(p.x / pps), y: Math.floor(p.y / pps) };
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

  return (
    <div className="relative w-full overflow-hidden rounded border border-black/10 dark:border-white/10 bg-neutral-950">
      <svg
        ref={svgRef}
        viewBox={`${view.minX} ${view.minY} ${view.width} ${view.height}`}
        className={isTable ? "w-full aspect-[16/10]" : "w-full aspect-[4/3]"}
        style={paintMode ? { cursor: "crosshair", touchAction: "none" } : undefined}
        role="img"
        aria-label="แผนที่ฉาก"
        onPointerDown={(e) => {
          if (!paintMode) return;
          const cell = pointToCell(e.clientX, e.clientY);
          if (!cell) return;
          // จับ pointer ไว้ — ลากออกนอกแผนที่ชั่วคราวแล้วกลับเข้ามา เส้นยังต่อเนื่อง
          e.currentTarget.setPointerCapture?.(e.pointerId);
          lastPaintCell.current = cell;
          strokeRef.current = [cell];
          setStroke([cell]);
        }}
        onPointerMove={(e) => {
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
          if (dragCell === null) return;
          const cell = pointToCell(e.clientX, e.clientY);
          if (cell) setDragCell(cell);
        }}
        onPointerUp={(e) => {
          if (paintMode) {
            finishStroke();
            return;
          }
          if (dragCell !== null && dragTokenId !== null) {
            if (dropIsLegal) onTokenDrop?.(dragTokenId, dragCell);
          } else if (onEmptyCellClick) {
            // ไม่ได้ลากอะไรอยู่ = คลิกเปล่าบนแผนที่ → ใช้วาง token ใหม่ (เฉพาะช่องที่ยังว่าง
            // เพื่อไม่ให้วางทับตัวที่มีอยู่แล้วโดยไม่ตั้งใจ)
            const cell = pointToCell(e.clientX, e.clientY);
            if (cell && !occupiedKeys.has(`${cell.x},${cell.y}`)) onEmptyCellClick(cell);
          }
          setDragCell(null);
          setDragTokenId(null);
        }}
        onPointerLeave={() => {
          if (paintMode) {
            finishStroke();
            return;
          }
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
                cx={cellToPx(a.x)}
                cy={cellToPx(a.y)}
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

        {/* พื้นอยู่ใต้หมอก — ของ DM (หมอกโปร่ง) ที่อยู่ในพื้นที่ปิดจะดูจางลงเหมือน token ในหมอก */}
        {terrain && (
          <g>
            {terrain.difficult.map((c) => (
              <rect
                key={`difficult-${c.x},${c.y}`}
                x={cellCornerPx(c.x)}
                y={cellCornerPx(c.y)}
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
                x={cellCornerPx(c.x)}
                y={cellCornerPx(c.y)}
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
              x={cellCornerPx(c.x)}
              y={cellCornerPx(c.y)}
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
            cx={cellToPx(a.x)}
            cy={cellToPx(a.y)}
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
          const cx = cellToPx(t.x);
          const cy = cellToPx(t.y);

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
              x={cellCornerPx(c.x)}
              y={cellCornerPx(c.y)}
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
            cx={cellToPx(dragCell.x)}
            cy={cellToPx(dragCell.y)}
            r={tokenRadius}
            fill={dropIsLegal ? "#22c55e" : "#ef4444"}
            fillOpacity={0.35}
            stroke={dropIsLegal ? "#22c55e" : "#ef4444"}
            strokeWidth={strokeWidth * 1.5}
            style={{ pointerEvents: "none" }}
          />
        )}
      </svg>

      {showEmptyHint && emptyHint && (
        <p className="absolute inset-0 flex items-center justify-center px-6 text-center text-sm opacity-60">
          {emptyHint}
        </p>
      )}
    </div>
  );
}

"use client";

import { useEffect, useRef, useState } from "react";
import { healthRatio, isPointRevealed, sceneBounds, tokenInitial } from "@/lib/scene-view";
import type { FogArea, SceneToken } from "@/lib/scene-types";

/** หนึ่งช่องบนกริดกินกี่พิกเซลตอนวาด — ใช้เมื่อฉากยังไม่ได้ตั้งกริดเอง */
const DEFAULT_PIXELS_PER_SQUARE = 50;

export interface ReachableCell {
  x: number;
  y: number;
  costFeet: number;
}

/**
 * Sprint G Part 6/10 — แผนที่ + token + หมอก + การลาก token
 *
 * **ตั้งใจให้โง่**: ไม่ fetch ไม่รู้จัก role ไม่ตัดสินใจว่าใครควรเห็นอะไร — วาดสิ่งที่ถูกส่งเข้ามาเท่านั้น
 * การกรองตาม role เกิดที่ server แล้ว (design doc §13.3) การให้ component นี้รู้จัก role ด้วยจะ
 * กลายเป็นจุดที่สองที่ตัดสินใจเรื่องเดียวกัน แล้ววันหนึ่งสองจุดจะไม่ตรงกัน
 *
 * ## หน่วยพิกัด — จุดแปลงหน่วยจุดเดียวของทั้งระบบ
 * **`x`/`y` ของ token กับหมอกคือ "ช่อง" ไม่ใช่พิกเซล** (1 ช่อง = 5 ฟุตตาม SRD) — เก็บเป็นช่อง
 * เพราะกฎต้องเป๊ะ: ช่องเป็นจำนวนเต็ม คิดระยะแบบ Chebyshev แล้วได้คำตอบตายตัว ไม่มีปัดเศษ และตรงกับ
 * engine ฝั่ง server (Part 8/9/10) ที่คิดทุกอย่างเป็นช่อง
 *
 * ไฟล์นี้เป็น **ที่เดียวในระบบที่แปลงช่อง→พิกเซล** (ผ่าน `cellToPx`) — บทเรียนจาก Owlbear Rodeo
 * ที่ทุกคนต้องเรียก `grid.getDistance()` ตัวเดียวแทนการคิดเลขเอง · ก่อนหน้านี้ไฟล์นี้เคยตีความว่า
 * x/y เป็นพิกเซลบนรูป ซึ่งขัดกับ engine และจะพังทันทีที่มีแผนที่จริง (token ห่างกัน 1 ช่อง = 40 พิกเซล
 * จะถูกคิดเป็น 40 ช่อง = 200 ฟุต) — แก้ให้ตรงกันแล้วในรอบนี้
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
  draggableEntityId,
  onDropCell,
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
  /** entity ที่ผู้ใช้คนนี้ลากได้ — ตัวอื่นลากไม่ได้เลย */
  draggableEntityId?: string;
  onDropCell?: (cell: { x: number; y: number }) => void;
}) {
  const [measured, setMeasured] = useState<{ url: string; w: number; h: number } | null>(null);
  const [dragCell, setDragCell] = useState<{ x: number; y: number } | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);

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

  const showEmptyHint = tokens.length === 0 && revealedAreas.length === 0;
  const canDrag = !!draggableEntityId && !!onDropCell && !!reachable;
  const reachableKeys = new Set((reachable ?? []).map((c) => `${c.x},${c.y}`));

  /** พิกัดบนจอ → ช่องบนกริด (ผ่าน CTM ของ SVG เพื่อให้ถูกต้องทุกขนาดจอ/ทุกการซูม) */
  const pointToCell = (clientX: number, clientY: number) => {
    const svg = svgRef.current;
    const ctm = svg?.getScreenCTM();
    if (!svg || !ctm) return null;
    const p = new DOMPoint(clientX, clientY).matrixTransform(ctm.inverse());
    return { x: Math.floor(p.x / pps), y: Math.floor(p.y / pps) };
  };

  const dropIsLegal = dragCell !== null && reachableKeys.has(`${dragCell.x},${dragCell.y}`);

  return (
    <div className="relative w-full overflow-hidden rounded border border-black/10 dark:border-white/10 bg-neutral-950">
      <svg
        ref={svgRef}
        viewBox={`${view.minX} ${view.minY} ${view.width} ${view.height}`}
        className={isTable ? "w-full aspect-[16/10]" : "w-full aspect-[4/3]"}
        role="img"
        aria-label="แผนที่ฉาก"
        onPointerMove={(e) => {
          if (dragCell === null) return;
          const cell = pointToCell(e.clientX, e.clientY);
          if (cell) setDragCell(cell);
        }}
        onPointerUp={() => {
          if (dragCell !== null && dropIsLegal) onDropCell?.(dragCell);
          setDragCell(null);
        }}
        onPointerLeave={() => setDragCell(null)}
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
              x={c.x * pps}
              y={c.y * pps}
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
          const name = nameOf(t.entityId);
          const ratio = healthRatio(t.currentHp, t.maxHp);
          const hiddenFromPlayers =
            fogStyle === "translucent" && !isPointRevealed(t.x, t.y, revealedAreas);
          const isMonster = t.entityType === "Monster";
          const isDraggable = canDrag && t.entityId === draggableEntityId;
          const cx = cellToPx(t.x);
          const cy = cellToPx(t.y);

          return (
            <g key={t.id}>
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
                        setDragCell({ x: t.x, y: t.y });
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

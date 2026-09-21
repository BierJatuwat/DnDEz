"use client";

import { useState } from "react";
import { SceneMap } from "./SceneMap";
import { TerrainLegend } from "./TerrainLegend";
import { useSceneTokens } from "@/lib/use-scene-tokens";
import { useCampaignMonsters } from "@/lib/use-srd";
import { tokenLabel } from "@/lib/srd-view";
import { useSceneTerrain } from "@/lib/use-scene-terrain";
import { useReachable } from "@/lib/use-reachable";
import { useMovement } from "@/lib/use-movement";
import type { FogArea, SceneToken } from "@/lib/scene-types";
import type { CharacterSummary } from "@/lib/campaign-types";
import type { SceneTerrain, TerrainPaintTool } from "@/lib/terrain-paint";

const modeBtn = "rounded px-3 py-1.5 text-sm border transition-colors";
const selectClass =
  "rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";

/** เครื่องมือวาดพื้น — ชื่อที่ DM เห็นเป็นภาษาเกม ไม่ใช่ชื่อ enum ของ API */
const PAINT_TOOLS: { tool: TerrainPaintTool; label: string }[] = [
  { tool: "Difficult", label: "หนองน้ำ / พื้นเดินยาก" },
  { tool: "Blocked", label: "กำแพง" },
  { tool: "Clear", label: "ยางลบ" },
];

type Mode = "setup" | "rules" | "terrain";

/**
 * Sprint H Part 2 — เครื่องมือแผนที่ของ DM: วาง / ขยับ / ลบ token · Sprint H Part 6 — วาดพื้นเดินยาก/กำแพง
 *
 * ## สองเส้นทางการขยับ token ที่ **ห้ามยุบรวมกัน** (design doc §14.2)
 * - **จัดฉาก** (`setup`) → `PATCH .../tokens/{id}` · ย้ายอะไรไปไหนก็ได้ ไม่คิดระยะ ไม่เกิด OA
 *   ใช้ตอนเซ็ตอัพก่อนเริ่ม หรือตอนเล่าเรื่องแล้วอยากย้ายฉากทั้งดุ้น
 * - **เดินตามกฎ** (`rules`) → `POST .../movement/commit` · หัก speed จริง เกิด OA จริง
 *   ใช้ตอน NPC เดินในการต่อสู้
 *
 * ทั้งสองอย่างเป็น "การขยับ token" เหมือนกันในสายตาผู้ใช้ แต่ผลต่างกันสิ้นเชิง — UI จึงต้องบอกว่า
 * ตอนนี้อยู่โหมดไหนแบบที่มองปราดเดียวรู้ ไม่ใช่ให้ DM เดาจากพฤติกรรมที่เกิดขึ้นหลังปล่อยเมาส์แล้ว
 *
 * ## โหมดวาดพื้น (`terrain`) — Sprint H Part 6
 * เลือกเครื่องมือแล้วลากทับช่อง ปล่อยเมาส์ = บันทึกทันที (`PATCH .../terrain` DM-only) · ระหว่างวาด
 * ลาก/วาง token ไม่ได้ เพราะใช้การลากเหมือนกัน · พื้นแสดงให้เห็นทุกโหมดเสมอ (ไม่ใช่แค่ตอนวาด)
 * เพราะ DM ต้องเห็นว่ากำแพงอยู่ตรงไหนตอนจัด token ด้วย · ผู้เล่นเห็นเฉพาะส่วนที่เปิดหมอกแล้ว (server กรอง)
 *
 * ## ทำไมโหมด "เดินตามกฎ" ต้องเลือกตัวก่อนถึงจะลากได้
 * ขอบเขต `reachable` คำนวณจาก **ช่องที่ตัวนั้นยืนอยู่** — ถ้าเปิดให้ลากทุกตัวพร้อมกัน จะต้องดึง
 * ขอบเขตของ NPC ทุกตัวในฉากมาเก็บไว้ล่วงหน้า ทั้งที่ DM ขยับทีละตัวอยู่แล้ว · เลือกก่อนแล้วค่อยลาก
 * จึงตรงกับทั้งวิธีเล่นจริงและกับที่ฝั่งผู้เล่นทำอยู่ (ตัวละครที่กำลังสั่งทีละตัว)
 */
export function DmMapTools({
  campaignId,
  sessionId,
  sceneId,
  tokens,
  revealedAreas,
  terrain,
  imageUrl,
  pixelsPerSquare,
  characters,
  nameOf,
}: {
  campaignId: string;
  sessionId: string;
  sceneId: string | null;
  tokens: SceneToken[];
  revealedAreas: FogArea[];
  terrain: SceneTerrain;
  imageUrl: string | null;
  pixelsPerSquare: number;
  characters: CharacterSummary[];
  nameOf: (entityId: string) => string | undefined;
}) {
  const [mode, setMode] = useState<Mode>("setup");
  // ค่าในช่องเลือกเป็น "ชนิด:id" เพราะ id ของตัวละครกับมอนสเตอร์อยู่คนละตาราง — ส่ง id เปล่าไปจะไม่รู้ว่า
  // ต้องบอก server ว่า entityType อะไร (และ id ซ้ำกันข้ามตารางได้ในทางทฤษฎี)
  const [placingValue, setPlacingValue] = useState<string>("");
  const [movingCharacterId, setMovingCharacterId] = useState<string>("");
  const [paintTool, setPaintTool] = useState<TerrainPaintTool>("Difficult");

  const { placeToken, moveToken, removeToken } = useSceneTokens(campaignId, sceneId);
  // มอนสเตอร์ของแคมเปญ — ดึงเฉพาะตอนอยู่โหมดจัดฉากซึ่งเป็นโหมดเดียวที่ใช้ลิสต์นี้
  const { monsters } = useCampaignMonsters(campaignId, mode === "setup");
  const { paint } = useSceneTerrain(campaignId, sceneId);
  const reachable = useReachable(campaignId, sessionId, mode === "rules" ? movingCharacterId || null : null);
  const movement = useMovement(campaignId, sessionId);

  /** NPC ที่ DM คุมได้เท่านั้น — `movement/commit` ใช้ `RequireCharacterActorAsync` ซึ่ง DM ผ่านได้
   *  เฉพาะตัวที่ `ControlledByDm = true` (ตัวละครผู้เล่นต้องให้เจ้าตัวเดินเอง) */
  const dmControlled = characters.filter((c) => c.controlledByDm);

  const movingTokenIds = tokens
    .filter((t) => t.entityType === "Character" && t.entityId === movingCharacterId)
    .map((t) => t.id);

  const draggableTokenIds =
    mode === "setup" ? tokens.map((t) => t.id) : mode === "rules" ? movingTokenIds : [];

  const handleTokenDrop = (tokenId: string, cell: { x: number; y: number }) => {
    if (mode === "setup") {
      moveToken.mutate({ tokenId, x: cell.x, y: cell.y });
      return;
    }

    // เดินตามกฎ — ส่ง **เส้นทางที่ server คำนวณมาให้** ไม่ประกอบเส้นทางเอง (กันการส่งทางที่
    // เลี่ยง OA หรือลัดผ่านสิ่งกีดขวาง) เหมือนฝั่งผู้เล่นเป๊ะ
    const square = reachable.squares.find((s) => s.x === cell.x && s.y === cell.y);
    if (!square || !movingCharacterId) return;
    movement.commit.mutate({ characterId: movingCharacterId, path: square.path, disengage: false });
  };

  const handleEmptyCellClick = (cell: { x: number; y: number }) => {
    if (mode !== "setup" || !placingValue) return;
    const [entityType, entityId] = placingValue.split(":");
    if (entityType !== "Character" && entityType !== "Monster") return;
    placeToken.mutate({ entityType, entityId, x: cell.x, y: cell.y });
  };

  const placedEntityIds = new Set(tokens.map((t) => t.entityId));

  const modeButton = (value: Mode, label: string, activeClass: string) => (
    <button
      type="button"
      onClick={() => setMode(value)}
      aria-pressed={mode === value}
      className={`${modeBtn} ${
        mode === value ? activeClass : "border-black/20 dark:border-white/20 opacity-60"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div className="space-y-3">
      {/* สลับโหมด — ต้องเห็นปราดเดียวว่าการลากครั้งต่อไปจะให้ผลแบบไหน */}
      <div className="flex flex-wrap items-center gap-2">
        {modeButton("setup", "จัดฉาก", "border-amber-500 bg-amber-500/15 text-amber-600 dark:text-amber-400")}
        {modeButton("rules", "เดินตามกฎ", "border-green-500 bg-green-500/15 text-green-700 dark:text-green-400")}
        {modeButton("terrain", "วาดพื้น", "border-orange-700 bg-orange-700/15 text-orange-700 dark:text-orange-400")}
        <span className="text-xs opacity-60">
          {mode === "setup"
            ? "ลากได้ทุกตัว วางที่ไหนก็ได้ — ไม่หักระยะ ไม่โดนสวน"
            : mode === "rules"
              ? "ลากได้เฉพาะ NPC ที่เลือก ภายในระยะที่เดินไหว — หักระยะจริงและโดนสวนได้"
              : "ลากทับช่องบนแผนที่ ปล่อยเมาส์แล้วบันทึกทันที — ผู้เล่นเห็นเฉพาะส่วนที่เปิดหมอกแล้ว"}
        </span>
      </div>

      {mode === "terrain" && (
        <div className="flex flex-wrap items-center gap-2" role="group" aria-label="เครื่องมือวาดพื้น">
          {PAINT_TOOLS.map(({ tool, label }) => (
            <button
              key={tool}
              type="button"
              onClick={() => setPaintTool(tool)}
              aria-pressed={paintTool === tool}
              className={`${modeBtn} ${
                paintTool === tool
                  ? "border-orange-700 bg-orange-700/15"
                  : "border-black/20 dark:border-white/20 opacity-60"
              }`}
            >
              {label}
            </button>
          ))}
          {paint.isPending && <span className="text-xs opacity-60">กำลังบันทึก...</span>}
        </div>
      )}

      <SceneMap
        tokens={tokens}
        revealedAreas={revealedAreas}
        imageUrl={imageUrl}
        nameOf={nameOf}
        fogStyle="translucent"
        emptyHint="ยังไม่มีอะไรบนแผนที่ — เลือกตัวละครด้านล่างแล้วคลิกบนแผนที่เพื่อวาง"
        pixelsPerSquare={pixelsPerSquare}
        reachable={reachable.squares}
        draggableTokenIds={draggableTokenIds}
        onTokenDrop={handleTokenDrop}
        onEmptyCellClick={mode === "setup" ? handleEmptyCellClick : undefined}
        constrainToReachable={mode === "rules"}
        terrain={terrain}
        paintTool={mode === "terrain" ? paintTool : null}
        onPaint={mode === "terrain" ? (cells) => paint.mutate({ tool: paintTool, cells }) : undefined}
      />

      <TerrainLegend terrain={terrain} />

      {mode === "setup" && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm opacity-60">วางตัวใหม่:</span>
          <select
            value={placingValue}
            onChange={(e) => setPlacingValue(e.target.value)}
            className={selectClass}
            aria-label="ตัวที่จะวางลงแผนที่"
          >
            <option value="">— เลือกตัวละครหรือมอนสเตอร์ —</option>
            <optgroup label="ตัวละคร / NPC">
              {characters.map((c) => (
                <option key={c.id} value={`Character:${c.id}`}>
                  {c.name}
                  {c.controlledByDm ? " [NPC]" : ""}
                  {placedEntityIds.has(c.id) ? " (วางแล้ว)" : ""}
                </option>
              ))}
            </optgroup>
            {/* มอนสเตอร์ตัวเดียวกันวางซ้ำได้หลายตัวบนแผนที่ (ก็อบลิน 3 ตัวใช้แม่แบบเดียวกัน) จึงไม่ขึ้น
                ว่า "วางแล้ว" เหมือนตัวละคร — บอกจำนวนที่วางไปแล้วแทนเพื่อให้ DM นับถูก */}
            <optgroup label="มอนสเตอร์ในแคมเปญ">
              {monsters.map((m) => {
                const placed = tokens.filter((t) => t.entityId === m.id).length;
                return (
                  <option key={m.id} value={`Monster:${m.id}`}>
                    {m.name} (AC {m.armorClass} · HP {m.hitPoints})
                    {placed > 0 ? ` — บนแผนที่ ${placed} ตัว` : ""}
                  </option>
                );
              })}
            </optgroup>
          </select>
          <span className="text-xs opacity-60">
            {placingValue
              ? "→ คลิกช่องว่างบนแผนที่เพื่อวาง"
              : monsters.length === 0
                ? "เลือกตัวก่อน แล้วคลิกช่องว่างบนแผนที่ — ยังไม่มีมอนสเตอร์ในแคมเปญ เพิ่มได้จากคลังมอนสเตอร์ด้านล่าง"
                : "เลือกตัวก่อน แล้วคลิกช่องว่างบนแผนที่"}
          </span>
        </div>
      )}

      {mode === "rules" && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm opacity-60">ให้ใครเดิน:</span>
          <select
            value={movingCharacterId}
            onChange={(e) => setMovingCharacterId(e.target.value)}
            className={selectClass}
            aria-label="NPC ที่จะเดิน"
          >
            <option value="">— เลือก NPC —</option>
            {dmControlled.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
          {movingCharacterId && (
            <span className="text-xs opacity-60">
              ลาก token ของตัวนี้ได้ในระยะ {reachable.budgetFeet} ฟุต
            </span>
          )}
          {dmControlled.length === 0 && (
            <span className="text-xs opacity-60">ยังไม่มี NPC ที่ DM คุมในแคมเปญนี้</span>
          )}
        </div>
      )}

      {/* รายการ token บนฉาก + ปุ่มลบ — ทำเป็นลิสต์แทนการคลิกบน token โดยตรง เพราะการคลิกกับการ
          เริ่มลากใช้ event เดียวกัน แยกเจตนาไม่ได้ชัด ๆ · ลิสต์ยังช่วยให้ DM เห็นว่ามีอะไรบนแผนที่บ้าง */}
      {tokens.length > 0 && (
        <div className="space-y-1">
          <p className="text-xs uppercase tracking-wider opacity-50">token บนฉากนี้</p>
          <ul className="space-y-1 text-sm">
            {tokens.map((t) => (
              <li key={t.id} className="flex items-center justify-between gap-2">
                <span className="truncate">
                  {tokenLabel(t, nameOf)}
                  <span className="ml-2 tabular-nums opacity-50">
                    ({Math.round(t.x)}, {Math.round(t.y)})
                  </span>
                </span>
                <button
                  type="button"
                  onClick={() => removeToken.mutate(t.id)}
                  disabled={removeToken.isPending}
                  className="rounded border border-red-500/40 px-2 py-0.5 text-xs text-red-600 dark:text-red-400 disabled:opacity-50"
                >
                  ลบ
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {(placeToken.isError || moveToken.isError || removeToken.isError) && (
        <p className="text-xs text-red-500">คำสั่งล่าสุดไม่สำเร็จ ลองใหม่อีกครั้ง</p>
      )}
      {paint.isError && (
        <p className="text-xs text-red-500">วาดพื้นไม่สำเร็จ — ลองลากใหม่อีกครั้ง</p>
      )}
      {movement.commit.isError && (
        <p className="text-xs text-red-500">เดินไม่สำเร็จ — อาจเกินระยะหรือเส้นทางไม่ถูกต้อง</p>
      )}
    </div>
  );
}

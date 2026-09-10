"use client";

import { useState } from "react";
import { useMovement, type MovementPreview, type PathPoint } from "@/lib/use-movement";

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";
const inputClass =
  "w-20 rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";

/**
 * Sprint G Part 10 (ส่วน ง) — วางเส้นทางแล้ว **เห็นราคากับคำเตือนก่อนกดจริง**
 *
 * spatial-play-design §3.4: *"ทางนี้ใช้ 25 จาก 30 ฟุต และจะโดนโจมตีสวนตอนออกจากระยะก็อบลิน"*
 * → **"OA ไม่ใช่กับดักที่มือใหม่เหยียบโดยไม่รู้ตัว แต่เป็นข้อมูลที่เห็นก่อนตัดสินใจ"**
 *
 * ⚠️ **ทำไมกรอกพิกัดเป็นตัวเลข ไม่ใช่คลิกบนแผนที่**: หน่วยของพิกัด token ยังไม่ถูกนิยามให้ตรงกัน
 * ทั้งระบบ — `SceneMap` เรนเดอร์โดยถือว่าเป็น **พิกเซลบนรูปแผนที่** แต่ engine ของ Part 8/9/10
 * คิดระยะโดยถือว่าเป็น **ช่องกริด** (1 ช่อง = 5 ฟุต) · ถ้าทำ UI คลิกแผนที่ตอนนี้จะเป็นการล็อก
 * ข้อสมมติที่ขัดกันเองลงไปในโค้ดอีกชั้น จึงเลือกให้ผู้เล่นระบุ "ช่อง" ตรง ๆ ไปก่อนอย่างซื่อสัตย์
 * จนกว่าเจ้าของโปรเจกต์จะตัดสินว่าหน่วยคืออะไร แล้วค่อยทำตัวเลือกบนแผนที่ให้ตรงกัน
 */
export function MovementPlanner({
  campaignId,
  sessionId,
  characterId,
  characterName,
}: {
  campaignId: string;
  sessionId: string;
  characterId: string;
  characterName: string;
}) {
  const { preview, commit } = useMovement(campaignId, sessionId);
  const [path, setPath] = useState<PathPoint[]>([]);
  const [x, setX] = useState("0");
  const [y, setY] = useState("0");
  const [disengage, setDisengage] = useState(false);
  const [result, setResult] = useState<MovementPreview | null>(null);

  const addStep = () => {
    const next = { x: Number(x) || 0, y: Number(y) || 0 };
    const updated = [...path, next];
    setPath(updated);
    if (updated.length >= 2) {
      preview.mutate(
        { characterId, path: updated, disengage },
        { onSuccess: setResult },
      );
    }
  };

  const reset = () => {
    setPath([]);
    setResult(null);
  };

  const toggleDisengage = (value: boolean) => {
    setDisengage(value);
    if (path.length >= 2) {
      preview.mutate({ characterId, path, disengage: value }, { onSuccess: setResult });
    }
  };

  return (
    <section className={sectionClass}>
      <h2 className="font-medium">วางเส้นทางเดิน</h2>
      <p className="text-xs opacity-50">
        ระบุช่องทีละก้าว (ก้าวแรกคือช่องที่ {characterName} ยืนอยู่ตอนนี้) — 1 ช่อง = 5 ฟุต
      </p>

      <div className="flex flex-wrap items-center gap-2">
        <input
          value={x}
          onChange={(e) => setX(e.target.value)}
          className={inputClass}
          aria-label="ช่องแนวนอน"
          inputMode="numeric"
        />
        <input
          value={y}
          onChange={(e) => setY(e.target.value)}
          className={inputClass}
          aria-label="ช่องแนวตั้ง"
          inputMode="numeric"
        />
        <button
          type="button"
          onClick={addStep}
          className="rounded border border-black/20 dark:border-white/20 px-3 py-1 text-sm"
        >
          เพิ่มก้าว
        </button>
        {path.length > 0 && (
          <button type="button" onClick={reset} className="text-sm underline opacity-60">
            เริ่มใหม่
          </button>
        )}
      </div>

      {path.length > 0 && (
        <p className="text-sm tabular-nums opacity-70">
          เส้นทาง: {path.map((p) => `(${p.x},${p.y})`).join(" → ")}
        </p>
      )}

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={disengage}
          onChange={(e) => toggleDisengage(e.target.checked)}
        />
        ถอยอย่างระวัง (Disengage) — ไม่โดนสวน แต่ใช้ action ของเทิร์นนี้
      </label>

      {result && (
        <div className="space-y-2 border-t border-black/10 dark:border-white/10 pt-3">
          <p className="text-sm">
            ใช้ระยะ <strong className="tabular-nums">{result.costFeet}</strong> ฟุต
            {!result.isAffordable && (
              <span className="ml-2 text-red-600 dark:text-red-400">— ระยะไม่พอในเทิร์นนี้</span>
            )}
          </p>

          {/* หัวใจของ §3.4 — บอกราคาของทางเลือกให้ครบ "ก่อน" กด ไม่ใช่ให้เหยียบแล้วค่อยรู้ */}
          {result.warnings.map((w) => (
            <p
              key={w.threatId}
              className="flex items-start gap-2 rounded border border-red-500/40 px-2 py-1 text-xs text-red-600 dark:text-red-400"
            >
              <span aria-hidden="true">⚠</span>
              <span>{w.message}</span>
            </p>
          ))}

          {result.warnings.length === 0 && (
            <p className="text-xs text-green-700 dark:text-green-400">
              เส้นทางนี้ไม่มีใครได้โจมตีสวน
            </p>
          )}

          <button
            type="button"
            onClick={() =>
              commit.mutate({ characterId, path, disengage }, { onSuccess: reset })
            }
            disabled={!result.isAffordable || commit.isPending}
            className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
          >
            เดินตามเส้นทางนี้
          </button>
        </div>
      )}

      {preview.isError && (
        <p className="text-xs text-red-500">
          เส้นทางไม่ถูกต้อง — แต่ละก้าวต้องอยู่ติดกัน (ห่างกันไม่เกิน 1 ช่องทุกทิศ)
        </p>
      )}
      {commit.isError && <p className="text-xs text-red-500">เดินไม่สำเร็จ ลองใหม่อีกครั้ง</p>}
    </section>
  );
}

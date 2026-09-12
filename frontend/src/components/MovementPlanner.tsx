"use client";

import { useState } from "react";
import { useMovement, type MovementPreview, type PathPoint } from "@/lib/use-movement";

const sectionClass = "rounded border border-amber-500/60 p-4 space-y-3";

/** การเดินที่ลาก token ไปวางแล้ว แต่ server เตือนว่ามีราคา — รอผู้เล่นตัดสินใจ */
export interface PendingMove {
  characterId: string;
  /** เส้นทางที่ server คำนวณมาให้ของช่องที่วาง (จาก reachable) — client ไม่ประกอบเอง */
  path: PathPoint[];
  preview: MovementPreview;
}

/**
 * Sprint H Part 4 (frontend) — ยืนยันการเดินที่มีราคา · เดิมไฟล์นี้คือฟอร์มกรอกพิกัดทีละก้าว
 *
 * spatial-play-design §3.4: **"OA ไม่ใช่กับดักที่มือใหม่เหยียบโดยไม่รู้ตัว แต่เป็นข้อมูลที่เห็นก่อน
 * ตัดสินใจ"**
 *
 * **ไม่มีช่องกรอกแล้ว** — ผู้เล่นเดินด้วยการลาก token บนแผนที่อย่างเดียว · PlayerView ส่งเส้นทางของ
 * ช่องที่วางไป preview ก่อนทุกครั้ง: ไม่มีคำเตือน → เดินเลยเหมือนเดิม · มีคำเตือน → เปิดพาเนลนี้
 *
 * **ทำไมไม่ลบทิ้งทั้งไฟล์** ตาม "เอาช่องกรอกออก": ฟอร์มเดิมเป็นที่เดียวในหน้าผู้เล่นที่ (1) เห็นคำเตือน
 * OA ก่อนกดเดิน และ (2) เลือก Disengage ได้ — การลากแบบเดิม commit ทันทีด้วย `disengage: false`
 * ถ้าลบฟอร์มเฉย ๆ ทั้งสองอย่างจะหายไปจากหน้าผู้เล่น
 */
export function MovementPlanner({
  campaignId,
  sessionId,
  pending,
  onClose,
}: {
  campaignId: string;
  sessionId: string;
  pending: PendingMove;
  onClose: () => void;
}) {
  const { preview, commit } = useMovement(campaignId, sessionId);
  const [disengage, setDisengage] = useState(false);
  const [result, setResult] = useState<MovementPreview>(pending.preview);

  const destination = pending.path[pending.path.length - 1];

  // Disengage เปลี่ยนคำตอบของ server (ไม่มีใครได้สวน) จึงถามใหม่ทุกครั้งที่สลับ ไม่เดาผลเอง
  const toggleDisengage = (value: boolean) => {
    setDisengage(value);
    preview.mutate(
      { characterId: pending.characterId, path: pending.path, disengage: value },
      { onSuccess: setResult },
    );
  };

  return (
    <section className={sectionClass} aria-label="ยืนยันการเดิน">
      <h2 className="font-medium">ยืนยันการเดิน</h2>

      <p className="text-sm">
        ไปช่อง ({destination.x}, {destination.y}) · ใช้ระยะ{" "}
        <strong className="tabular-nums">{result.costFeet}</strong> ฟุต
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
        <p className="text-xs text-green-700 dark:text-green-400">เส้นทางนี้ไม่มีใครได้โจมตีสวน</p>
      )}

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={disengage}
          onChange={(e) => toggleDisengage(e.target.checked)}
        />
        ถอยอย่างระวัง (Disengage) — ไม่โดนสวน แต่ใช้ action ของเทิร์นนี้
      </label>

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={() =>
            commit.mutate(
              { characterId: pending.characterId, path: pending.path, disengage },
              { onSuccess: onClose },
            )
          }
          disabled={!result.isAffordable || commit.isPending || preview.isPending}
          className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
        >
          เดินตามเส้นทางนี้
        </button>
        <button
          type="button"
          onClick={onClose}
          className="rounded border border-black/20 dark:border-white/20 px-3 py-1.5 text-sm"
        >
          ไม่เดินแล้ว
        </button>
      </div>

      {preview.isError && <p className="text-xs text-red-500">คำนวณเส้นทางใหม่ไม่สำเร็จ ลองอีกครั้ง</p>}
      {commit.isError && <p className="text-xs text-red-500">เดินไม่สำเร็จ ลองใหม่อีกครั้ง</p>}
    </section>
  );
}

"use client";

import { useState } from "react";
import { describeSummaryError } from "@/lib/ai-summary-view";
import { useSessionRecap } from "@/lib/use-recap";

const textareaClass =
  "w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";
const smallBtn =
  "rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-50";

/**
 * สรุปเซสชันที่เลือก — สองฉบับ: ของผู้เล่น ("ตอนที่แล้ว…" ขึ้นหน้าผู้เล่น) กับของ DM (ลับ, ที่ปรึกษา AI
 * อ่านเป็นความจำข้ามเซสชัน) · AI สร้างให้ได้ หรือ DM เขียน/แก้เองได้ (ไม่ต้องมี API key)
 */
export function SessionRecapPanel({ campaignId, sessionId }: { campaignId: string; sessionId: string }) {
  const { recap, isLoading, generate, save } = useSessionRecap(campaignId, sessionId);
  const [editing, setEditing] = useState(false);
  const [playerText, setPlayerText] = useState("");
  const [dmText, setDmText] = useState("");

  const startEditing = () => {
    setPlayerText(recap?.playerVersionText ?? "");
    setDmText(recap?.dmVersionText ?? "");
    setEditing(true);
  };

  const regenerate = () => {
    if (recap && !window.confirm("สร้างใหม่จะแทนที่สรุปเดิมทั้งสองฉบับ (รวมที่แก้เองไว้) ต่อไหม?")) return;
    generate.mutate();
  };

  return (
    <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="font-medium">สรุปเซสชันนี้</h2>
        {recap && (
          <span className="text-[11px] tabular-nums opacity-50">
            อัปเดต {new Date(recap.createdAt).toLocaleString("th-TH", { dateStyle: "short", timeStyle: "short" })}
          </span>
        )}
      </div>

      {editing ? (
        <div className="space-y-2">
          <label className="block space-y-1">
            <span className="text-xs opacity-70">ฉบับผู้เล่น — ทุกคนในโต๊ะเห็น ห้ามมีความลับ</span>
            <textarea
              value={playerText}
              onChange={(e) => setPlayerText(e.target.value)}
              aria-label="สรุปฉบับผู้เล่น"
              rows={4}
              maxLength={8000}
              className={textareaClass}
            />
          </label>
          <label className="block space-y-1">
            <span className="text-xs opacity-70">ฉบับ DM — ลับ · ที่ปรึกษา AI ใช้เป็นความจำ</span>
            <textarea
              value={dmText}
              onChange={(e) => setDmText(e.target.value)}
              aria-label="สรุปฉบับ DM"
              rows={6}
              maxLength={8000}
              className={textareaClass}
            />
          </label>
          <div className="flex gap-2">
            <button
              type="button"
              disabled={save.isPending}
              onClick={() =>
                save.mutate(
                  { dmVersionText: dmText, playerVersionText: playerText },
                  { onSuccess: () => setEditing(false) },
                )
              }
              className="rounded bg-sky-700 px-3 py-1 text-xs text-white disabled:opacity-50"
            >
              บันทึกสรุป
            </button>
            <button type="button" className={smallBtn} onClick={() => setEditing(false)}>
              ยกเลิก
            </button>
          </div>
          {save.isError && <p className="text-xs text-red-500">{describeSummaryError(save.error)}</p>}
        </div>
      ) : (
        <>
          {!isLoading && !recap && (
            <p className="text-sm opacity-60">
              ยังไม่มีสรุป — ให้ AI สรุปจากบทสนทนาของเซสชันนี้ หรือเขียนเองก็ได้
            </p>
          )}
          {recap && (
            <div className="space-y-2">
              <div className="space-y-1">
                <p className="text-[11px] uppercase tracking-wider opacity-50">ฉบับผู้เล่น (ทุกคนเห็น)</p>
                <p className="whitespace-pre-wrap text-sm">
                  {recap.playerVersionText || (
                    <span className="opacity-50">
                      ว่าง — เซสชันนี้ยังไม่มีเหตุการณ์ที่ผู้เล่นเห็น ผู้เล่นจะไม่เห็นสรุปนี้
                    </span>
                  )}
                </p>
              </div>
              <details className="rounded border border-amber-500/30 p-2">
                <summary className="cursor-pointer text-[11px] uppercase tracking-wider text-amber-700 dark:text-amber-400">
                  ฉบับ DM (ลับ)
                </summary>
                <p className="mt-1 whitespace-pre-wrap text-sm opacity-90">{recap.dmVersionText}</p>
              </details>
            </div>
          )}
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={regenerate}
              disabled={generate.isPending}
              className="rounded bg-emerald-700 px-3 py-1 text-xs text-white disabled:opacity-50"
            >
              {generate.isPending ? "AI กำลังสรุป..." : recap ? "สร้างใหม่ด้วย AI" : "ให้ AI สรุป"}
            </button>
            <button type="button" className={smallBtn} onClick={startEditing}>
              {recap ? "แก้ไขเอง" : "เขียนเอง"}
            </button>
          </div>
          {generate.isError && (
            <p className="text-xs text-red-500">{describeSummaryError(generate.error)}</p>
          )}
        </>
      )}
    </section>
  );
}

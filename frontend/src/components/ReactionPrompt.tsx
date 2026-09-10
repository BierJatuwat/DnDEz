"use client";

import { useEffect, useState } from "react";
import {
  isExpired,
  secondsRemaining,
  urgency,
  type ReactionPrompt as Prompt,
} from "@/lib/reaction-prompt";
import { useMovement } from "@/lib/use-movement";

const TONES = {
  calm: "border-green-500/50",
  hurry: "border-yellow-500/70",
  critical: "border-red-500",
} as const;

const BAR_TONES = {
  calm: "bg-green-500",
  hurry: "bg-yellow-500",
  critical: "bg-red-500",
} as const;

/**
 * Sprint G Part 10 (ส่วน ง) — prompt "จะสวนไหม" พร้อมนับถอยหลัง
 *
 * **DoD ของ Part นี้**: *"ผู้เล่นเดินออกจากระยะก็อบลิน → คนคุมก็อบลินเห็น prompt พร้อมนับถอยหลัง
 * โดยไม่ต้องมีใครจำกฎ OA ได้เอง"* — ข้อความจึงอธิบายเป็นเหตุการณ์ ไม่ใช่ชื่อกฎ และบอกด้วยว่า
 * ถ้าไม่ทำอะไรจะเกิดอะไรขึ้น (ไม่ใช่ปล่อยให้เดาว่าเงียบไว้แล้วจะยังไง)
 *
 * ⚠️ **ไม่ต้องเช็คสิทธิ์ฝั่ง client** — event นี้ยิงมาที่กลุ่มของผู้ใช้คนเดียว
 * (`campaign:{id}:user:{userId}`) ถ้ามาถึงเราแปลว่า server ตัดสินแล้วว่าเรามีสิทธิ์
 *
 * นับถอยหลังจาก **deadline สัมบูรณ์ของ server** ไม่ใช่นับเองจาก timeoutSeconds — ตัวเลขบนจอจึงตรง
 * กับตัวที่ sweeper ฝั่ง server ใช้ตัดสินจริง แม้ผู้ใช้จะเพิ่งต่อกลับมาหลังเน็ตหลุด
 */
export function ReactionPrompt({
  campaignId,
  sessionId,
  prompt,
  onResolved,
}: {
  campaignId: string;
  sessionId: string;
  prompt: Prompt;
  onResolved: () => void;
}) {
  const { respond } = useMovement(campaignId, sessionId);
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 250);
    return () => clearInterval(id);
  }, []);

  const remaining = secondsRemaining(prompt.deadlineUtc, now);
  const expired = isExpired(prompt.deadlineUtc, now);
  const tone = urgency(remaining, prompt.timeoutSeconds);

  // หมดเวลาแล้ว server จะปิดให้เอง (sweeper) — ฝั่งนี้แค่เก็บ prompt ลงเงียบ ๆ ไม่ต้องยิงอะไรไปอีก
  useEffect(() => {
    if (expired) onResolved();
  }, [expired, onResolved]);

  if (expired) return null;

  const pct = prompt.timeoutSeconds > 0
    ? Math.min(100, Math.max(0, (remaining / prompt.timeoutSeconds) * 100))
    : 0;

  const answer = (useReaction: boolean) =>
    respond.mutate(
      { reactionId: prompt.reactionId, useReaction },
      { onSuccess: onResolved },
    );

  return (
    <section className={`rounded border-2 ${TONES[tone]} p-4 space-y-3`}>
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="font-semibold">{prompt.threatName} มีโอกาสโจมตีสวน!</h2>
        <span className="tabular-nums text-lg font-bold">{remaining}</span>
      </div>

      <p className="text-sm opacity-80">
        มีคนกำลังเดินออกจากระยะเอื้อมของ{prompt.threatName} — จะให้{prompt.threatName}ฟันสวนหนึ่งครั้งไหม?
      </p>

      {/* แถบเวลาที่เหลือ — อ่านได้จากมุมตาโดยไม่ต้องอ่านตัวเลข */}
      <div className="h-1.5 w-full rounded bg-black/10 dark:bg-white/10" aria-hidden="true">
        <div
          className={`h-1.5 rounded transition-[width] duration-200 ${BAR_TONES[tone]}`}
          style={{ width: `${pct}%` }}
        />
      </div>

      <div className="flex gap-2">
        <button
          type="button"
          onClick={() => answer(true)}
          disabled={respond.isPending}
          className="rounded bg-red-600 px-3 py-1.5 text-white disabled:opacity-50"
        >
          สวนกลับ
        </button>
        <button
          type="button"
          onClick={() => answer(false)}
          disabled={respond.isPending}
          className="rounded border border-black/20 dark:border-white/20 px-3 py-1.5 disabled:opacity-50"
        >
          ปล่อยไป
        </button>
      </div>

      {/* บอกผลของการไม่ทำอะไร — กัน pacing สะดุดเพราะคนไม่กล้ากดอะไรเลย (player-side-design §4) */}
      <p className="text-xs opacity-50">ไม่กดอะไรภายในเวลา = ปล่อยไป แล้วเกมเดินต่อเอง</p>

      {respond.isError && (
        <p className="text-xs text-red-500">ส่งคำตอบไม่สำเร็จ — อาจหมดเวลาไปแล้ว</p>
      )}
    </section>
  );
}

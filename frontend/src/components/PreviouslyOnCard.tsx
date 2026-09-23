"use client";

import { useLatestRecap } from "@/lib/use-recap";

/**
 * "ตอนที่แล้ว…" — ฉบับผู้เล่นของ recap ล่าสุดในแคมเปญ
 *
 * server ส่งมาเฉพาะ `playerVersionText` (ฉบับ DM เป็น null เสมอสำหรับผู้เล่น) และข้าม recap ที่ฉบับผู้เล่น
 * ว่างให้แล้ว — ไม่มี recap = ไม่แสดงการ์ดเลย ไม่ใช่โชว์กล่องว่าง
 */
export function PreviouslyOnCard({ campaignId }: { campaignId: string }) {
  const { recap } = useLatestRecap(campaignId);
  if (!recap?.playerVersionText) return null;

  return (
    <section className="rounded border border-amber-500/30 bg-amber-500/[0.04] p-4 space-y-2">
      <h2 className="text-xs uppercase tracking-wider text-amber-700 dark:text-amber-400">ตอนที่แล้ว…</h2>
      <p className="whitespace-pre-wrap text-sm leading-relaxed">{recap.playerVersionText}</p>
    </section>
  );
}

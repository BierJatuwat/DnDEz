"use client";

import { useState } from "react";
import { ApiError } from "@/lib/api";
import { useAdvisor } from "@/lib/use-advisor";

/**
 * แชทที่ปรึกษาส่วนตัวของ DM (Part C ของแผน "DM AI Assistant", 2026-09-22)
 *
 * **ไม่ผูกกับ session** ต่างจากช่อง "ปรึกษา AI" เดิมใน `DmConsole` ที่ต้องเลือกเซสชันก่อนถึงจะโผล่ —
 * อันนี้ใช้ได้ตลอดเวลา รวมถึงตอนที่ยังไม่มีเซสชันสักอันในแคมเปญ (เตรียมตัวก่อนเล่น/วางแผนตอนถัดไป)
 *
 * ประวัติมาจาก server ทุกครั้ง (ไม่ใช่ state ในหน้าจอ) จึงอ่านต่อได้ข้ามวัน/ข้ามเครื่อง
 */
export function AdvisorPanel({ campaignId }: { campaignId: string }) {
  const { messages, isLoading, isError, error, send } = useAdvisor(campaignId);
  const [input, setInput] = useState("");

  // ผู้เล่นที่เปิดหน้า DM ได้ (ยังไม่มีระบบแยก route ตาม role) จะได้ 403 — บอกให้ชัดแทนที่จะโชว์
  // ช่องแชทที่กดแล้วพังเงียบ ๆ
  const isForbidden = isError && error instanceof ApiError && error.status === 403;

  return (
    <section className="rounded border border-black/10 dark:border-white/15 p-3 space-y-2">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="font-medium">ที่ปรึกษา DM (ส่วนตัว)</h2>
        <span className="text-[11px] opacity-50">ใช้ได้แม้ยังไม่เปิดเซสชัน · ผู้เล่นไม่เห็น</span>
      </div>

      {isForbidden ? (
        <p className="text-sm opacity-60">เฉพาะ DM เจ้าของแคมเปญเท่านั้นที่ใช้ช่องนี้ได้</p>
      ) : (
        <>
          <div className="flex gap-2">
            <textarea
              value={input}
              onChange={(e) => setInput(e.target.value)}
              placeholder="ถามเรื่องกฎ/บาลานซ์/วางแผนตอนต่อไป..."
              aria-label="ข้อความถึงที่ปรึกษา DM"
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <button
              onClick={() => {
                const text = input.trim();
                if (!text) return;
                send.mutate(text);
                setInput("");
              }}
              disabled={send.isPending || !input.trim()}
              className="rounded bg-emerald-600 px-3 py-1.5 text-white disabled:opacity-50"
            >
              ส่ง
            </button>
          </div>

          {send.isError && (
            <p className="text-sm text-red-500">
              {send.error instanceof ApiError && send.error.status === 400
                ? "ส่งไม่สำเร็จ — ตรวจว่าตั้ง Anthropic API key ไว้แล้วหรือยัง"
                : "ส่งไม่สำเร็จ ลองใหม่อีกครั้ง"}
            </p>
          )}

          <ul className="space-y-1 text-sm max-h-64 overflow-y-auto">
            {messages.map((m) => (
              <li key={m.id} className={m.role === "Ai" ? "opacity-90 text-emerald-400" : "opacity-100"}>
                <span className="font-mono text-xs opacity-50">[{m.role === "Ai" ? "ai" : "dm"}]</span>{" "}
                <span className="whitespace-pre-wrap">{m.content}</span>
              </li>
            ))}
            {send.isPending && <li className="opacity-50 italic">กำลังคิด...</li>}
            {!isLoading && messages.length === 0 && !send.isPending && (
              <li className="opacity-50">ยังไม่มีบทสนทนา — ลองถามเรื่องบาลานซ์เอนเคาน์เตอร์หรือกฎที่ไม่แน่ใจดู</li>
            )}
          </ul>
        </>
      )}
    </section>
  );
}

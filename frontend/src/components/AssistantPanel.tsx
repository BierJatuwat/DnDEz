"use client";

import { useEffect, useRef, useState } from "react";
import { ApiError } from "@/lib/api";
import { describeSummaryError } from "@/lib/ai-summary-view";
import {
  activityStatus,
  type AssistantActivity,
  type AssistantTurn,
  type PendingApproval,
} from "@/lib/assistant-view";
import { useAssistant } from "@/lib/use-assistant";

const TONE_CLASS = {
  ok: "text-emerald-700 dark:text-emerald-400",
  bad: "text-red-600 dark:text-red-400",
  muted: "opacity-60",
} as const;

const smallBtn =
  "rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-50";

/**
 * ผู้ช่วย DM ช่องเดียว — ถามข้อมูลได้และสั่งงานได้หลายขั้น (แทน "ที่ปรึกษา DM" + "ปรึกษา AI" เดิม)
 *
 * การขออนุญาตมาจาก autonomy dial ฝั่ง server: งานที่ต้องให้ DM ตัดสินขึ้นเป็นการ์ดท้ายแชท · ข้อความที่จะส่งถึง
 * ผู้เล่นขึ้นเป็นช่องให้แก้ก่อนกดอนุมัติเสมอ
 */
export function AssistantPanel({
  campaignId,
  sessionId,
  sessionLabel,
}: {
  campaignId: string;
  sessionId: string | null;
  sessionLabel: string | null;
}) {
  const assistant = useAssistant(campaignId, sessionId);
  const [input, setInput] = useState("");
  const listRef = useRef<HTMLDivElement>(null);

  const isForbidden =
    assistant.isError && assistant.error instanceof ApiError && assistant.error.status === 403;
  const busy = assistant.send.isPending || assistant.decide.isPending;
  const pending = assistant.pendingRun;

  // เลื่อนลงล่างสุดเมื่อมีข้อความ/การ์ดใหม่ — แชทยาวแล้วของใหม่อยู่ล่างเสมอ
  useEffect(() => {
    listRef.current?.scrollTo({ top: listRef.current.scrollHeight });
  }, [assistant.messages.length, pending?.pending.length, busy]);

  const submit = () => {
    const text = input.trim();
    if (!text || busy) return;
    assistant.send.mutate(text);
    setInput("");
  };

  return (
    <section className="rounded border border-black/10 dark:border-white/15 p-3 space-y-2">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="font-medium">ผู้ช่วย DM</h2>
        <span className="text-[11px] opacity-50">ผู้เล่นไม่เห็นช่องนี้</span>
      </div>
      <p className="text-[11px] opacity-70">
        {sessionId
          ? `เชื่อมกับเซสชัน ${sessionLabel ?? ""} — ทอยเต๋า/คุมไฟต์/แผนที่ได้`
          : "ยังไม่ได้เลือกเซสชัน — ถามกฎ เตรียมเกม จดบันทึกได้ (เลือกเซสชันก่อนถ้าจะให้ทำงานในเกม)"}
      </p>

      {isForbidden ? (
        <p className="text-sm opacity-60">เฉพาะ DM เจ้าของแคมเปญเท่านั้นที่ใช้ช่องนี้ได้</p>
      ) : (
        <>
          <div ref={listRef} className="max-h-96 space-y-2 overflow-y-auto text-sm">
            {assistant.messages.map((m) =>
              m.role === "Ai" ? (
                <div key={m.id} className="space-y-1 rounded bg-emerald-500/[0.06] p-2">
                  <p className="whitespace-pre-wrap">{m.content}</p>
                  <ActivityList activity={m.activity} />
                </div>
              ) : (
                <p key={m.id} className="whitespace-pre-wrap px-2">
                  <span className="font-mono text-[10px] opacity-50">DM </span>
                  {m.content}
                </p>
              ),
            )}

            {pending && (
              <PendingRunCard
                run={pending}
                busy={busy}
                onDecide={(card, approve, editedText) =>
                  assistant.decide.mutate({
                    runId: pending.runId,
                    toolUseId: card.toolUseId,
                    approve,
                    editedText,
                  })
                }
              />
            )}

            {busy && <p className="px-2 italic opacity-60">กำลังทำงาน…</p>}
            {!assistant.isLoading && assistant.messages.length === 0 && !pending && !busy && (
              <p className="px-2 opacity-50">
                ลองถามกฎ ให้ช่วยวางแผน หรือสั่งงาน เช่น &quot;จดไว้ว่าปาร์ตี้ติดหนี้นายกฯ 200 gp&quot;
              </p>
            )}
          </div>

          {(assistant.send.isError || assistant.decide.isError) && (
            <p className="text-xs text-red-500">
              {describeTurnError(assistant.send.error ?? assistant.decide.error)}
            </p>
          )}

          <div className="flex gap-2">
            <textarea
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                  e.preventDefault();
                  submit();
                }
              }}
              placeholder="ถามหรือสั่งงาน… (Ctrl+Enter ส่ง)"
              aria-label="ข้อความถึงผู้ช่วย DM"
              maxLength={4000}
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <button
              type="button"
              onClick={submit}
              disabled={busy || !input.trim()}
              className="rounded bg-emerald-700 px-3 py-1.5 text-white disabled:opacity-50"
            >
              ส่ง
            </button>
          </div>
          {pending && (
            <p className="text-[11px] opacity-60">ส่งคำสั่งใหม่ตอนนี้ = ยกเลิกงานที่รออนุมัติอยู่</p>
          )}
        </>
      )}
    </section>
  );
}

function ActivityList({ activity }: { activity: AssistantActivity[] }) {
  if (activity.length === 0) return null;
  return (
    <ul className="space-y-0.5 border-t border-black/10 dark:border-white/10 pt-1 text-xs">
      {activity.map((a) => {
        const status = activityStatus(a.status);
        return (
          <li key={a.toolUseId} className="flex gap-2">
            <span className={`shrink-0 ${TONE_CLASS[status.tone]}`}>{status.label}</span>
            <span className="opacity-80">{a.summary}</span>
          </li>
        );
      })}
    </ul>
  );
}

function PendingRunCard({
  run,
  busy,
  onDecide,
}: {
  run: AssistantTurn;
  busy: boolean;
  onDecide: (card: PendingApproval, approve: boolean, editedText?: string) => void;
}) {
  return (
    <div className="space-y-2 rounded border border-amber-500/40 bg-amber-500/[0.05] p-2">
      {run.text && <p className="whitespace-pre-wrap">{run.text}</p>}
      <ActivityList activity={run.activity} />
      <p className="text-[11px] uppercase tracking-wider text-amber-700 dark:text-amber-400">
        รอ DM อนุมัติ ({run.pending.length})
      </p>
      {run.pending.map((card) => (
        <ApprovalCard key={card.toolUseId} card={card} busy={busy} onDecide={onDecide} />
      ))}
    </div>
  );
}

function ApprovalCard({
  card,
  busy,
  onDecide,
}: {
  card: PendingApproval;
  busy: boolean;
  onDecide: (card: PendingApproval, approve: boolean, editedText?: string) => void;
}) {
  const [text, setText] = useState(card.editableText ?? "");
  const editable = card.editableText !== null;

  return (
    <div className="space-y-1 rounded border border-black/10 dark:border-white/10 p-2">
      <p className="text-sm">{card.description}</p>
      {editable && (
        <textarea
          value={text}
          onChange={(e) => setText(e.target.value)}
          rows={4}
          aria-label="ข้อความที่จะส่งถึงผู้เล่น"
          className="w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm"
        />
      )}
      <div className="flex gap-2">
        <button
          type="button"
          disabled={busy || (editable && !text.trim())}
          onClick={() => onDecide(card, true, editable ? text : undefined)}
          className="rounded bg-emerald-700 px-2 py-1 text-xs text-white disabled:opacity-50"
        >
          {editable ? "ส่งถึงผู้เล่น" : "อนุมัติ"}
        </button>
        <button type="button" disabled={busy} onClick={() => onDecide(card, false)} className={smallBtn}>
          ปฏิเสธ
        </button>
      </div>
    </div>
  );
}

function describeTurnError(err: unknown): string {
  if (err instanceof ApiError && err.status === 409) {
    return "การ์ดนี้ถูกตัดสินไปแล้ว หรืองานถูกยกเลิกไปแล้ว";
  }
  return describeSummaryError(err);
}

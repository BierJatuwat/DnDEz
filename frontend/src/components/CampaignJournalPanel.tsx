"use client";

import { useState } from "react";
import { ApiError } from "@/lib/api";
import { describeSummaryError } from "@/lib/ai-summary-view";
import { useJournal, type JournalEntry } from "@/lib/use-journal";

const inputClass =
  "w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";
const smallBtn =
  "rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-50";

const AUTHOR_LABEL: Record<string, { label: string; className: string }> = {
  Dm: { label: "DM", className: "border-sky-500/40 text-sky-700 dark:text-sky-400" },
  Ai: { label: "AI สรุป", className: "border-emerald-500/40 text-emerald-700 dark:text-emerald-400" },
  System: { label: "ระบบ", className: "border-black/20 dark:border-white/20 opacity-70" },
};

/**
 * บันทึกแคมเปญ — ชั้นความจำรายละเอียดของ AI (ค้นผ่าน tool ตอน DM ปรึกษา) และสมุดโน้ตของ DM เอง
 *
 * ผูกกับแคมเปญ ไม่ใช่ session จึงอยู่นอกเงื่อนไข `selectedSessionId` — แต่ปุ่ม "ให้ AI สรุปเซสชัน" ต้องมี
 * เซสชันที่เลือกไว้ก่อน (สรุปจาก log ของเซสชันนั้น)
 */
export function CampaignJournalPanel({
  campaignId,
  selectedSessionId,
}: {
  campaignId: string;
  selectedSessionId: string | null;
}) {
  const journal = useJournal(campaignId);
  const [composing, setComposing] = useState(false);
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [linkSession, setLinkSession] = useState(true);

  const isForbidden = journal.isError && journal.error instanceof ApiError && journal.error.status === 403;

  const submitNew = () => {
    journal.create.mutate(
      {
        title: title.trim(),
        content: content.trim(),
        sessionId: linkSession && selectedSessionId ? selectedSessionId : null,
      },
      {
        onSuccess: () => {
          setTitle("");
          setContent("");
          setComposing(false);
        },
      },
    );
  };

  return (
    <section className="rounded border border-black/10 dark:border-white/15 p-3 space-y-2">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="font-medium">บันทึกแคมเปญ</h2>
        <span className="text-[11px] opacity-50">DM เท่านั้น · AI ค้นได้ตอนปรึกษา</span>
      </div>

      {isForbidden ? (
        <p className="text-sm opacity-60">เฉพาะ DM เจ้าของแคมเปญเท่านั้นที่เห็นบันทึกนี้</p>
      ) : (
        <>
          <div className="flex flex-wrap gap-2">
            <button type="button" className={smallBtn} onClick={() => setComposing((v) => !v)}>
              {composing ? "ยกเลิก" : "+ เขียนบันทึก"}
            </button>
            <button
              type="button"
              className={smallBtn}
              disabled={!selectedSessionId || journal.aiSummary.isPending}
              onClick={() => selectedSessionId && journal.aiSummary.mutate(selectedSessionId)}
            >
              {journal.aiSummary.isPending ? "AI กำลังสรุป..." : "ให้ AI สรุปเซสชันที่เลือก"}
            </button>
          </div>
          {!selectedSessionId && (
            <p className="text-[11px] opacity-50">เลือกเซสชันด้านซ้ายก่อน ถึงจะให้ AI สรุปได้</p>
          )}
          {journal.aiSummary.isError && (
            <p className="text-xs text-red-500">{describeSummaryError(journal.aiSummary.error)}</p>
          )}

          {composing && (
            <div className="space-y-2 rounded border border-black/10 dark:border-white/10 p-2">
              <input
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                placeholder="หัวข้อ เช่น ข้อตกลงกับนายกเทศมนตรี"
                maxLength={200}
                aria-label="หัวข้อบันทึก"
                className={inputClass}
              />
              <textarea
                value={content}
                onChange={(e) => setContent(e.target.value)}
                placeholder="เกิดอะไรขึ้น ใครพูดอะไร อะไรที่ต้องจำไว้"
                maxLength={8000}
                rows={4}
                aria-label="เนื้อหาบันทึก"
                className={inputClass}
              />
              {selectedSessionId && (
                <label className="flex items-center gap-2 text-xs opacity-80">
                  <input
                    type="checkbox"
                    checked={linkSession}
                    onChange={(e) => setLinkSession(e.target.checked)}
                  />
                  ผูกกับเซสชันที่เลือกอยู่ (ใช้ตอนสร้างสรุปเซสชัน)
                </label>
              )}
              <button
                type="button"
                onClick={submitNew}
                disabled={!title.trim() || !content.trim() || journal.create.isPending}
                className="rounded bg-sky-700 px-3 py-1 text-sm text-white disabled:opacity-50"
              >
                บันทึก
              </button>
              {journal.create.isError && (
                <p className="text-xs text-red-500">{describeSummaryError(journal.create.error)}</p>
              )}
            </div>
          )}

          <ul className="space-y-2 max-h-80 overflow-y-auto">
            {journal.entries.map((entry) => (
              <JournalEntryItem key={entry.id} entry={entry} journal={journal} />
            ))}
            {!journal.isLoading && journal.entries.length === 0 && (
              <li className="text-sm opacity-50">
                ยังไม่มีบันทึก — จบไฟต์ที่มีมอนสเตอร์ ระบบจะสรุปลงที่นี่ให้เอง
              </li>
            )}
          </ul>
        </>
      )}
    </section>
  );
}

function JournalEntryItem({
  entry,
  journal,
}: {
  entry: JournalEntry;
  journal: ReturnType<typeof useJournal>;
}) {
  const [expanded, setExpanded] = useState(false);
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(entry.title);
  const [content, setContent] = useState(entry.content);
  const author = AUTHOR_LABEL[entry.authoredBy] ?? AUTHOR_LABEL.System;
  const isSaving = journal.update.isPending && journal.update.variables?.id === entry.id;
  const isDeleting = journal.remove.isPending && journal.remove.variables === entry.id;

  if (editing) {
    return (
      <li className="space-y-2 rounded border border-black/10 dark:border-white/10 p-2">
        <input
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          maxLength={200}
          aria-label="แก้หัวข้อบันทึก"
          className={inputClass}
        />
        <textarea
          value={content}
          onChange={(e) => setContent(e.target.value)}
          maxLength={8000}
          rows={6}
          aria-label="แก้เนื้อหาบันทึก"
          className={inputClass}
        />
        <div className="flex gap-2">
          <button
            type="button"
            disabled={!title.trim() || !content.trim() || isSaving}
            onClick={() =>
              journal.update.mutate(
                { id: entry.id, title: title.trim(), content: content.trim() },
                { onSuccess: () => setEditing(false) },
              )
            }
            className="rounded bg-sky-700 px-3 py-1 text-xs text-white disabled:opacity-50"
          >
            บันทึกการแก้ไข
          </button>
          <button
            type="button"
            className={smallBtn}
            onClick={() => {
              setTitle(entry.title);
              setContent(entry.content);
              setEditing(false);
            }}
          >
            ยกเลิก
          </button>
        </div>
      </li>
    );
  }

  return (
    <li className="rounded border border-black/10 dark:border-white/10 p-2 space-y-1">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
        className="flex w-full items-start justify-between gap-2 text-left"
      >
        <span className="text-sm font-medium">{entry.title}</span>
        <span className="flex shrink-0 items-center gap-1">
          <span className={`rounded border px-1.5 text-[10px] ${author.className}`}>{author.label}</span>
          <span className="text-[10px] tabular-nums opacity-50">
            {new Date(entry.createdAt).toLocaleDateString("th-TH", { day: "numeric", month: "short" })}
          </span>
        </span>
      </button>
      <p className={`whitespace-pre-wrap text-xs opacity-80 ${expanded ? "" : "line-clamp-2"}`}>
        {entry.content}
      </p>
      {expanded && (
        <div className="flex gap-2 pt-1">
          <button type="button" className={smallBtn} onClick={() => setEditing(true)}>
            แก้ไข
          </button>
          <button
            type="button"
            className={`${smallBtn} text-red-600 dark:text-red-400`}
            disabled={isDeleting}
            onClick={() => {
              if (window.confirm(`ลบบันทึก "${entry.title}"? ลบแล้วกู้คืนไม่ได้`)) {
                journal.remove.mutate(entry.id);
              }
            }}
          >
            ลบ
          </button>
        </div>
      )}
    </li>
  );
}

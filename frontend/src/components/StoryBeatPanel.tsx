"use client";

import { useRef, useState } from "react";
import { ApiError } from "@/lib/api";
import {
  activationBlocker,
  checkImportFiles,
  IMPORT_ACCEPT,
  importErrorMessage,
  moveBeat,
  parseSecrets,
  serializeSecrets,
  splitLines,
  type BeatSecret,
  type StoryBeat,
} from "@/lib/story-beat-view";
import { useStoryBeats, type BeatInput } from "@/lib/use-story-beats";

const inputClass =
  "w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";
const smallBtn =
  "rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-40";
const primaryBtn = "rounded bg-sky-700 px-3 py-1 text-xs text-white disabled:opacity-50";

const PROGRESS_BADGE: Record<string, { label: string; className: string }> = {
  Active: { label: "▶ กำลังเล่น", className: "border-emerald-500/50 text-emerald-700 dark:text-emerald-400" },
  Passed: { label: "✓ ผ่านแล้ว", className: "border-black/20 dark:border-white/20 opacity-60" },
};
const PENDING_BADGE = { label: "รอตรวจ", className: "border-amber-500/50 text-amber-700 dark:text-amber-400" };

/**
 * เนื้อเรื่องของแคมเปญ — รายการฉาก (story beat) ที่ผู้ช่วย DM ใช้รู้ว่า "ตอนนี้เล่นถึงไหน / ฉากต่อไปคืออะไร"
 *
 * สองทางเข้า: DM พิมพ์เอง (ยืนยันทันที) หรืออัปโหลด PDF/ภาพให้ AI แยกฉาก (**รอตรวจ** — AI มองไม่เห็นจนกว่า DM จะ
 * ตรวจแล้วกดยืนยัน เพราะ AI อ่านเอกสารพลาดได้ และ "สรุปฉาก" ของฉากที่กำลังเล่นถูกส่งให้ AI ที่เล่าเรื่องให้ผู้เล่นฟังด้วย
 * ถ้าความลับหลุดเข้าช่องนั้นโดยไม่มีคนตรวจ ผู้เล่นจะได้ยินทันที)
 */
export function StoryBeatPanel({
  campaignId,
  selectedSessionId,
}: {
  campaignId: string;
  selectedSessionId: string | null;
}) {
  const story = useStoryBeats(campaignId);
  const fileInput = useRef<HTMLInputElement>(null);
  const [composing, setComposing] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);
  const [fileProblem, setFileProblem] = useState<string | null>(null);
  const [lastImport, setLastImport] = useState<number | null>(null);

  const isForbidden = story.isError && story.error instanceof ApiError && story.error.status === 403;
  const pending = story.beats.filter((b) => b.reviewStatus !== "Approved");
  const ids = story.beats.map((b) => b.id);

  const startImport = (list: FileList | null) => {
    const files = Array.from(list ?? []);
    if (fileInput.current) fileInput.current.value = "";
    if (files.length === 0) return;
    const problem = checkImportFiles(files);
    setFileProblem(problem);
    setLastImport(null);
    if (problem) return;
    story.importFiles.mutate(files, { onSuccess: (result) => setLastImport(result.beats.length) });
  };

  return (
    <section className="rounded border border-black/10 dark:border-white/15 p-3 space-y-2">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="font-medium">เนื้อเรื่อง</h2>
        <span className="text-[11px] opacity-50">DM เท่านั้น · ผู้ช่วย AI ใช้รู้ว่าเล่นถึงฉากไหน</span>
      </div>

      {isForbidden ? (
        <p className="text-sm opacity-60">เฉพาะ DM เจ้าของแคมเปญเท่านั้นที่เห็นเนื้อเรื่อง</p>
      ) : (
        <>
          <div className="flex flex-wrap gap-2">
            <button type="button" className={smallBtn} onClick={() => setComposing((v) => !v)}>
              {composing ? "ยกเลิก" : "+ เพิ่มฉาก"}
            </button>
            <button
              type="button"
              className={smallBtn}
              disabled={story.importFiles.isPending}
              onClick={() => fileInput.current?.click()}
            >
              อัปโหลด PDF / ภาพหน้าหนังสือ
            </button>
            <input
              ref={fileInput}
              type="file"
              accept={IMPORT_ACCEPT}
              multiple
              hidden
              aria-label="เลือกไฟล์เนื้อเรื่อง"
              onChange={(e) => startImport(e.target.files)}
            />
          </div>

          {story.importFiles.isPending && (
            <p className="text-xs text-sky-700 dark:text-sky-400" role="status">
              AI กำลังอ่านเอกสารและแยกเป็นฉาก… เอกสารยาวอาจใช้เวลาหลายนาที
            </p>
          )}
          {fileProblem && <p className="text-xs text-red-500">{fileProblem}</p>}
          {story.importFiles.isError && (
            <p className="text-xs text-red-500">{importErrorMessage(story.importFiles.error)}</p>
          )}
          {lastImport !== null && !story.importFiles.isPending && (
            <p className="text-xs text-emerald-700 dark:text-emerald-400" role="status">
              ได้ {lastImport} ฉากจากเอกสาร — ตรวจ/แก้แล้วกดยืนยันก่อน AI ถึงจะนำไปใช้
            </p>
          )}
          <p className="text-[11px] opacity-50">
            PDF ไม่เกิน 15MB (100 หน้า) หรือภาพถ่ายหน้าหนังสือได้ครั้งละ 10 ภาพ · AI ใช้ API key ของคุณอ่าน
          </p>

          {composing && (
            <BeatForm
              submitLabel="เพิ่มฉาก"
              busy={story.create.isPending}
              error={story.create.isError ? "เพิ่มฉากไม่สำเร็จ" : null}
              onCancel={() => setComposing(false)}
              onSubmit={(input) => story.create.mutate(input, { onSuccess: () => setComposing(false) })}
            />
          )}

          {pending.length > 0 && (
            <div className="flex flex-wrap items-center justify-between gap-2 rounded border border-amber-500/40 bg-amber-500/10 px-2 py-1.5">
              <p className="text-xs">
                มี {pending.length} ฉากรอตรวจ — AI ยังไม่เห็นจนกว่าจะกดยืนยัน
              </p>
              <button
                type="button"
                className={smallBtn}
                disabled={story.approve.isPending}
                onClick={() => {
                  if (window.confirm(`ยืนยันทั้ง ${pending.length} ฉากโดยไม่ตรวจทีละฉาก?`)) {
                    story.approve.mutate(pending.map((b) => b.id));
                  }
                }}
              >
                ยืนยันทั้งหมด
              </button>
            </div>
          )}

          <ol className="space-y-1.5 max-h-[28rem] overflow-y-auto">
            {story.beats.map((beat, index) => (
              <BeatRow
                key={beat.id}
                beat={beat}
                index={index}
                open={openId === beat.id}
                onToggle={() => setOpenId(openId === beat.id ? null : beat.id)}
                sessionId={selectedSessionId}
                story={story}
                canMoveUp={index > 0}
                canMoveDown={index < story.beats.length - 1}
                onMove={(delta) => {
                  const next = moveBeat(ids, beat.id, delta);
                  if (next) story.reorder.mutate(next);
                }}
              />
            ))}
            {!story.isLoading && story.beats.length === 0 && (
              <li className="text-sm opacity-50">
                ยังไม่มีฉาก — เพิ่มเองทีละฉาก หรืออัปโหลดเนื้อเรื่องให้ AI แยกให้
              </li>
            )}
          </ol>
          {story.setActive.isError && <p className="text-xs text-red-500">ตั้งฉากปัจจุบันไม่สำเร็จ</p>}
          {story.reorder.isError && <p className="text-xs text-red-500">จัดลำดับไม่สำเร็จ ลองรีเฟรชหน้า</p>}
        </>
      )}
    </section>
  );
}

function BeatRow({
  beat,
  index,
  open,
  onToggle,
  sessionId,
  story,
  canMoveUp,
  canMoveDown,
  onMove,
}: {
  beat: StoryBeat;
  index: number;
  open: boolean;
  onToggle: () => void;
  sessionId: string | null;
  story: ReturnType<typeof useStoryBeats>;
  canMoveUp: boolean;
  canMoveDown: boolean;
  onMove: (delta: -1 | 1) => void;
}) {
  const [editing, setEditing] = useState(false);
  const isPending = beat.reviewStatus !== "Approved";
  const badge = isPending ? PENDING_BADGE : PROGRESS_BADGE[beat.progress];
  const blocker = activationBlocker(beat, sessionId);
  const busy = story.reorder.isPending;

  if (editing) {
    return (
      <li>
        <BeatForm
          initial={beat}
          submitLabel={isPending ? "บันทึกและยืนยัน" : "บันทึกการแก้ไข"}
          busy={story.update.isPending || story.approve.isPending}
          error={story.update.isError ? "บันทึกไม่สำเร็จ" : null}
          onCancel={() => setEditing(false)}
          onSubmit={(input) =>
            story.update.mutate(
              { id: beat.id, ...input },
              {
                onSuccess: () => {
                  // แก้แล้วกดบันทึก = ตรวจแล้ว ไม่ต้องกดยืนยันซ้ำอีกรอบ
                  if (isPending) story.approve.mutate([beat.id]);
                  setEditing(false);
                },
              },
            )
          }
        />
      </li>
    );
  }

  const secrets = parseSecrets(beat.secrets);

  return (
    <li
      className={`rounded border p-2 space-y-1 ${
        beat.progress === "Active"
          ? "border-emerald-500/50 bg-emerald-500/5"
          : isPending
            ? "border-amber-500/40 border-dashed"
            : "border-black/10 dark:border-white/10"
      }`}
    >
      <div className="flex items-start gap-2">
        <span className="w-5 shrink-0 pt-0.5 text-right text-[11px] tabular-nums opacity-50">{index + 1}</span>
        <button
          type="button"
          onClick={onToggle}
          aria-expanded={open}
          className="flex min-w-0 flex-1 items-start justify-between gap-2 text-left"
        >
          <span className={`text-sm font-medium ${beat.progress === "Passed" ? "opacity-60" : ""}`}>
            {beat.title}
          </span>
          {badge && (
            <span className={`shrink-0 rounded border px-1.5 text-[10px] ${badge.className}`}>{badge.label}</span>
          )}
        </button>
        <span className="flex shrink-0 flex-col">
          <button
            type="button"
            className="px-1 text-[10px] leading-3 opacity-60 hover:opacity-100 disabled:opacity-20"
            disabled={!canMoveUp || busy}
            onClick={() => onMove(-1)}
            aria-label={`เลื่อน ${beat.title} ขึ้น`}
          >
            ▲
          </button>
          <button
            type="button"
            className="px-1 text-[10px] leading-3 opacity-60 hover:opacity-100 disabled:opacity-20"
            disabled={!canMoveDown || busy}
            onClick={() => onMove(1)}
            aria-label={`เลื่อน ${beat.title} ลง`}
          >
            ▼
          </button>
        </span>
      </div>

      {open && (
        <div className="space-y-2 pl-7">
          <BeatDetail label="สรุปฉาก" value={beat.summary} />
          <BeatDetail label="เป้าหมาย" value={beat.objective} />
          <BeatDetail label="เริ่มเมื่อ" value={beat.entryTrigger} />
          {beat.exitConditions.length > 0 && (
            <div>
              <p className="text-[11px] opacity-60">จบเมื่อ</p>
              <ul className="list-disc pl-4 text-xs">
                {beat.exitConditions.map((c, i) => (
                  <li key={i}>{c}</li>
                ))}
              </ul>
            </div>
          )}
          <BeatDetail label="โน้ต DM" value={beat.dmNotes} />
          {secrets.length > 0 && (
            <div>
              <p className="text-[11px] text-rose-700 dark:text-rose-400">ความลับ (ผู้เล่นยังไม่รู้)</p>
              <ul className="list-disc pl-4 text-xs">
                {secrets.map((s, i) => (
                  <li key={i}>
                    {s.text}
                    {s.revealCondition && <span className="opacity-60"> — เผยเมื่อ {s.revealCondition}</span>}
                  </li>
                ))}
              </ul>
            </div>
          )}

          <div className="flex flex-wrap gap-2 pt-1">
            {isPending && (
              <button
                type="button"
                className={primaryBtn}
                disabled={story.approve.isPending}
                onClick={() => story.approve.mutate([beat.id])}
              >
                ยืนยันฉากนี้
              </button>
            )}
            <button
              type="button"
              className={smallBtn}
              disabled={!!blocker || story.setActive.isPending}
              title={blocker ?? undefined}
              onClick={() => sessionId && story.setActive.mutate({ sessionId, beatId: beat.id })}
            >
              ตั้งเป็นฉากปัจจุบัน
            </button>
            <button type="button" className={smallBtn} onClick={() => setEditing(true)}>
              แก้ไข
            </button>
            <button
              type="button"
              className={`${smallBtn} text-red-600 dark:text-red-400`}
              disabled={story.remove.isPending}
              onClick={() => {
                if (window.confirm(`ลบฉาก "${beat.title}"? ลบแล้วกู้คืนไม่ได้`)) story.remove.mutate(beat.id);
              }}
            >
              ลบ
            </button>
          </div>
          {blocker && blocker !== "เป็นฉากปัจจุบันอยู่แล้ว" && (
            <p className="text-[11px] opacity-50">ตั้งเป็นฉากปัจจุบันไม่ได้: {blocker}</p>
          )}
        </div>
      )}
    </li>
  );
}

function BeatDetail({ label, value }: { label: string; value: string | null }) {
  if (!value?.trim()) return null;
  return (
    <div>
      <p className="text-[11px] opacity-60">{label}</p>
      <p className="whitespace-pre-wrap text-xs">{value}</p>
    </div>
  );
}

function BeatForm({
  initial,
  submitLabel,
  busy,
  error,
  onSubmit,
  onCancel,
}: {
  initial?: StoryBeat;
  submitLabel: string;
  busy: boolean;
  error: string | null;
  onSubmit: (input: BeatInput) => void;
  onCancel: () => void;
}) {
  const [title, setTitle] = useState(initial?.title ?? "");
  const [summary, setSummary] = useState(initial?.summary ?? "");
  const [objective, setObjective] = useState(initial?.objective ?? "");
  const [entryTrigger, setEntryTrigger] = useState(initial?.entryTrigger ?? "");
  const [exitConditions, setExitConditions] = useState((initial?.exitConditions ?? []).join("\n"));
  const [dmNotes, setDmNotes] = useState(initial?.dmNotes ?? "");
  const [secrets, setSecrets] = useState<BeatSecret[]>(parseSecrets(initial?.secrets));

  const updateSecret = (index: number, patch: Partial<BeatSecret>) =>
    setSecrets((list) => list.map((s, i) => (i === index ? { ...s, ...patch } : s)));

  return (
    <form
      className="space-y-2 rounded border border-black/10 dark:border-white/10 p-2"
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          title: title.trim(),
          summary: summary.trim(),
          objective: objective.trim(),
          entryTrigger: entryTrigger.trim(),
          exitConditions: splitLines(exitConditions),
          dmNotes: dmNotes.trim(),
          secrets: serializeSecrets(secrets),
        });
      }}
    >
      <Field label="ชื่อฉาก">
        <input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} required className={inputClass} />
      </Field>
      <Field label="สรุปฉาก" hint="AI ใช้เล่าให้ผู้เล่นฟังตอนฉากนี้กำลังเล่น — ห้ามใส่ความลับหรือจุดหักมุม">
        <textarea value={summary} onChange={(e) => setSummary(e.target.value)} rows={3} className={inputClass} />
      </Field>
      <div className="grid gap-2 sm:grid-cols-2">
        <Field label="เป้าหมายของปาร์ตี้">
          <input value={objective} onChange={(e) => setObjective(e.target.value)} className={inputClass} />
        </Field>
        <Field label="ฉากเริ่มเมื่อ">
          <input value={entryTrigger} onChange={(e) => setEntryTrigger(e.target.value)} className={inputClass} />
        </Field>
      </div>
      <Field label="ฉากจบเมื่อ" hint="หนึ่งบรรทัดต่อหนึ่งทางจบ">
        <textarea
          value={exitConditions}
          onChange={(e) => setExitConditions(e.target.value)}
          rows={2}
          className={inputClass}
        />
      </Field>
      <Field label="โน้ต DM" hint="เห็นแค่ DM กับผู้ช่วย AI — แรงจูงใจ NPC, มอนสเตอร์, สมบัติ, DC">
        <textarea value={dmNotes} onChange={(e) => setDmNotes(e.target.value)} rows={3} className={inputClass} />
      </Field>

      <fieldset className="space-y-1.5">
        <legend className="text-[11px] text-rose-700 dark:text-rose-400">ความลับ (ผู้เล่นยังไม่รู้)</legend>
        {secrets.map((secret, i) => (
          <div key={i} className="flex flex-wrap items-center gap-1.5">
            <input
              value={secret.text}
              onChange={(e) => updateSecret(i, { text: e.target.value })}
              placeholder="ความลับ"
              aria-label={`ความลับข้อ ${i + 1}`}
              className={`${inputClass} flex-[2] min-w-[8rem]`}
            />
            <input
              value={secret.revealCondition}
              onChange={(e) => updateSecret(i, { revealCondition: e.target.value })}
              placeholder="เผยเมื่อ (ไม่บังคับ)"
              aria-label={`เงื่อนไขเผยความลับข้อ ${i + 1}`}
              className={`${inputClass} flex-1 min-w-[6rem]`}
            />
            <button
              type="button"
              className={smallBtn}
              onClick={() => setSecrets((list) => list.filter((_, j) => j !== i))}
              aria-label={`ลบความลับข้อ ${i + 1}`}
            >
              ✕
            </button>
          </div>
        ))}
        <button
          type="button"
          className={smallBtn}
          onClick={() => setSecrets((list) => [...list, { text: "", revealCondition: "" }])}
        >
          + เพิ่มความลับ
        </button>
      </fieldset>

      <div className="flex gap-2">
        <button type="submit" disabled={!title.trim() || busy} className={primaryBtn}>
          {submitLabel}
        </button>
        <button type="button" className={smallBtn} onClick={onCancel}>
          ยกเลิก
        </button>
      </div>
      {error && <p className="text-xs text-red-500">{error}</p>}
    </form>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-0.5">
      <span className="text-[11px] opacity-70">{label}</span>
      {children}
      {hint && <span className="block text-[10px] opacity-50">{hint}</span>}
    </label>
  );
}

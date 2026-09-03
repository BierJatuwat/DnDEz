"use client";

import type { UseMutationResult } from "@tanstack/react-query";
import type {
  AutonomySetting,
  CharacterSummary,
  GameSession,
} from "@/lib/campaign-types";
import { AUTONOMY_LEVELS, AUTONOMY_SUBSYSTEMS } from "@/lib/campaign-types";
import type { ActionMessage } from "@/lib/action-feed";
import type { HubStatus, LogEntry } from "@/lib/console-store";

/**
 * Sprint F งานที่ 6a — section ที่ **หน้า DM กับหน้าผู้เล่นใช้ร่วมกัน** แยกออกมาจาก
 * `CampaignConsole.tsx` เดิมตอนแยกหน้า
 *
 * หมายเหตุ convention: Sprint E Part 2 เคยตั้งใจเขียนทุก section inline ในไฟล์เดียวเพราะตอนนั้นมี
 * หน้าเดียวจริง ๆ การแยกไฟล์จึงไม่ได้อะไร — พอแยกเป็นสองหน้าแล้ว ทางเลือกเหลือแค่ "แยกคอมโพเนนต์"
 * กับ "ก๊อป JSX สองชุด" ซึ่งอย่างหลังแย่กว่าชัดเจน จึงเปลี่ยน convention ตรงนี้โดยตั้งใจ
 */

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";

export function HubStatusPanel({
  hubStatus,
  joinedCampaignId,
}: {
  hubStatus: HubStatus;
  joinedCampaignId: string | null;
}) {
  return (
    <section className={sectionClass}>
      <h2 className="font-medium">
        SignalR:{" "}
        <span className={hubStatus === "connected" ? "text-green-500" : "text-yellow-500"}>
          {hubStatus}
        </span>
        {joinedCampaignId && (
          <span className="opacity-70"> · joined {joinedCampaignId.slice(0, 8)}</span>
        )}
      </h2>
    </section>
  );
}

export function SessionPanel({
  sessions,
  selectedSessionId,
  onSelect,
  onCreate,
  creating,
}: {
  sessions: GameSession[];
  selectedSessionId: string | null;
  onSelect: (id: string) => void;
  /** ไม่ส่งมา = ผู้เล่น (สร้างเซสชันไม่ได้ ต้องให้ DM สร้าง) */
  onCreate?: () => void;
  creating?: boolean;
}) {
  return (
    <section className={sectionClass}>
      <h2 className="font-medium">เซสชัน</h2>
      {onCreate && (
        <button
          onClick={onCreate}
          disabled={creating}
          className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
        >
          สร้างเซสชันใหม่ (Online)
        </button>
      )}
      <ul className="space-y-1">
        {sessions.map((s) => (
          <li key={s.id}>
            <button
              onClick={() => onSelect(s.id)}
              className={`w-full text-left rounded px-3 py-1.5 ${
                selectedSessionId === s.id
                  ? "bg-indigo-600/20"
                  : "hover:bg-black/5 dark:hover:bg-white/5"
              }`}
            >
              {s.id.slice(0, 8)} — {s.mode} / {s.status}
            </button>
          </li>
        ))}
        {sessions.length === 0 && (
          <li className="opacity-50 text-sm">
            {onCreate ? "ยังไม่มีเซสชัน" : "ยังไม่มีเซสชัน — รอ DM เปิดเซสชันก่อน"}
          </li>
        )}
      </ul>
    </section>
  );
}

/** HP เป็น null = NPC ที่ DM ปิดการมองเห็น stat ไว้ (Sprint F งานที่ 7) ไม่ใช่ "ไม่มีข้อมูล" */
function HpLabel({ character }: { character: CharacterSummary }) {
  if (character.currentHp === null || character.maxHp === null) {
    return <span className="text-xs opacity-50">HP ซ่อนอยู่</span>;
  }
  return (
    <span className={`text-xs ${character.currentHp <= 0 ? "text-red-500" : "opacity-70"}`}>
      {character.currentHp}/{character.maxHp} HP
      {character.currentTempHp ? ` (+${character.currentTempHp} temp)` : ""}
    </span>
  );
}

export function CharacterRosterPanel({
  title,
  characters,
  onCreate,
  creating,
  emptyLabel,
}: {
  title: string;
  characters: CharacterSummary[];
  /** ไม่ส่งมา = แสดงอย่างเดียว สร้างไม่ได้ */
  onCreate?: (name: string) => void;
  creating?: boolean;
  emptyLabel?: string;
}) {
  return (
    <section className={sectionClass}>
      <h2 className="font-medium">{title}</h2>
      {onCreate && (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            const form = e.currentTarget;
            const input = form.elements.namedItem("characterName") as HTMLInputElement;
            if (input.value.trim()) {
              onCreate(input.value.trim());
              input.value = "";
            }
          }}
          className="flex gap-2"
        >
          <input
            name="characterName"
            placeholder="ชื่อตัวละครใหม่"
            className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
          />
          <button
            type="submit"
            disabled={creating}
            className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
          >
            สร้าง
          </button>
        </form>
      )}
      <ul className="space-y-1">
        {characters.map((c) => (
          <li key={c.id} className="px-3 py-1.5">
            {c.name} <span className="opacity-50 text-xs">(level {c.level})</span>{" "}
            <HpLabel character={c} />
            {c.controlledByDm && (
              <span className="ml-1 text-xs text-indigo-500">[DM คุม]</span>
            )}
          </li>
        ))}
        {characters.length === 0 && (
          <li className="opacity-50 text-sm">{emptyLabel ?? "ยังไม่มีตัวละคร"}</li>
        )}
      </ul>
    </section>
  );
}

export function AutonomyDialPanel({
  settings,
  isError,
  editable,
  setLevel,
}: {
  settings: AutonomySetting[];
  isError: boolean;
  editable: boolean;
  setLevel?: UseMutationResult<
    AutonomySetting,
    Error,
    { subsystem: string; level: string },
    unknown
  >;
}) {
  return (
    <section className={sectionClass}>
      <h2 className="font-medium">Autonomy dial (AI มีอำนาจแค่ไหนต่อ subsystem)</h2>
      {isError && <p className="text-sm text-red-500">โหลด autonomy dial ไม่สำเร็จ</p>}
      <ul className="space-y-2">
        {AUTONOMY_SUBSYSTEMS.map((sub) => {
          const current =
            settings.find((s) => s.subsystem === sub.value)?.level ?? "Suggest";
          const isUpdating =
            !!setLevel?.isPending && setLevel.variables?.subsystem === sub.value;
          return (
            <li key={sub.value} className="flex items-center justify-between gap-2">
              <span className="text-sm">{sub.label}</span>
              {editable && setLevel ? (
                <select
                  value={current}
                  disabled={isUpdating}
                  onChange={(e) =>
                    setLevel.mutate({ subsystem: sub.value, level: e.target.value })
                  }
                  className="rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm disabled:opacity-50"
                >
                  {AUTONOMY_LEVELS.map((lvl) => (
                    <option key={lvl.value} value={lvl.value}>
                      {lvl.label}
                    </option>
                  ))}
                </select>
              ) : (
                <span className="text-sm opacity-70">
                  {AUTONOMY_LEVELS.find((l) => l.value === current)?.label ?? current}
                </span>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

export function DicePanel({
  expression,
  setExpression,
  onRoll,
  rolling,
}: {
  expression: string;
  setExpression: (v: string) => void;
  onRoll: () => void;
  rolling: boolean;
}) {
  return (
    <section className={sectionClass}>
      <h2 className="font-medium">ทอยลูกเต๋า</h2>
      <div className="flex gap-2">
        <input
          value={expression}
          onChange={(e) => setExpression(e.target.value)}
          className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
        />
        <button
          onClick={onRoll}
          disabled={rolling}
          className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
        >
          ทอย
        </button>
      </div>
    </section>
  );
}

export function ActionFeedList({ actionFeed }: { actionFeed: ActionMessage[] }) {
  return (
    <ul className="space-y-1 text-sm max-h-64 overflow-y-auto">
      {actionFeed.map((m) => (
        <li
          key={m.id}
          className={m.authorType === "Player" ? "opacity-100" : "opacity-90 text-indigo-400"}
        >
          <span className="font-mono text-xs">[{m.authorType}]</span> {m.content}
        </li>
      ))}
      {actionFeed.length === 0 && (
        <li className="opacity-50 text-sm">ยังไม่มี action ในเซสชันนี้</li>
      )}
    </ul>
  );
}

export function LogPanel({ log }: { log: LogEntry[] }) {
  return (
    <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-1">
      <h2 className="font-medium mb-2">Log</h2>
      <ul className="space-y-1 text-sm font-mono max-h-64 overflow-y-auto">
        {log.map((entry, i) => (
          <li key={i} className="opacity-80">
            <span className="opacity-50">{entry.at}</span> {entry.text}
          </li>
        ))}
      </ul>
    </section>
  );
}

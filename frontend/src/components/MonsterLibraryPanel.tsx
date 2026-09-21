"use client";

import { useState } from "react";
import { useCampaignMonsters, useSrdMonsterDetail, useSrdMonsters, MONSTER_PAGE_SIZE } from "@/lib/use-srd";
import {
  CR_FILTERS,
  abilityRows,
  difficultyLabel,
  difficultyTone,
  monsterActionGroups,
  monsterSubtitle,
  sortByDifficulty,
} from "@/lib/srd-view";
import { SrdAttribution } from "./SrdAttribution";
import type { SrdMonsterSummary } from "@/lib/srd-types";

const inputClass =
  "rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";
const chipClass = "rounded px-2 py-1 text-xs border transition-colors";
const cardClass =
  "flex flex-col gap-2 rounded-lg border border-black/10 dark:border-white/10 p-3 bg-black/[0.02] dark:bg-white/[0.02] text-left";

const TONE_CLASS: Record<string, string> = {
  muted: "border-black/20 dark:border-white/20 opacity-60",
  green: "border-green-500/40 text-green-700 dark:text-green-400",
  amber: "border-amber-500/40 text-amber-700 dark:text-amber-400",
  red: "border-red-500/40 text-red-600 dark:text-red-400",
};

/**
 * คลังมอนสเตอร์ SRD 5.2.1 ของ DM — ค้น → ดู stat block → กดเพิ่มเข้าแคมเปญ
 *
 * **ทำไมต้องมีขั้น "เพิ่มเข้าแคมเปญ" ไม่ใช่ลากจากคลังลงแผนที่ตรง ๆ**: คลัง SRD เป็นของกลางอ่านอย่างเดียว
 * (ทั้งระบบแชร์แถวเดียวกัน) ส่วน token บนแผนที่ต้องชี้ไปที่แม่แบบ **ของแคมเปญนั้น** ที่ DM แก้ค่าได้เอง
 * — ดู doc comment ของ `SrdMonster` ฝั่ง C# · ขั้นนี้คือการคัดลอก ไม่ใช่การอ้างอิง
 *
 * **novice check**: DM ที่ไม่รู้ว่า "CR" คืออะไร ยังเลือกได้ถูก เพราะมีป้ายภาษาคน ("อ่อน" / "อันตราย")
 * และตัวกรองความยากเขียนว่า "มือใหม่ (ถึง 1/4)" แทนการให้กรอกเลข CR เอง
 *
 * กดเพิ่มตัวที่เพิ่มไปแล้วไม่พัง — API คืนตัวเดิม (`created: false`) และการ์ดขึ้นว่า "อยู่ในแคมเปญแล้ว"
 */
export function MonsterLibraryPanel({ campaignId }: { campaignId: string }) {
  const [query, setQuery] = useState("");
  const [crMax, setCrMax] = useState("1");
  const [page, setPage] = useState(0);
  const [openKey, setOpenKey] = useState<string | null>(null);

  const { monsters, total, attribution, isLoading, isError } = useSrdMonsters({ query, crMax, page });
  const { monsters: campaignMonsters, copyFromSrd } = useCampaignMonsters(campaignId);

  const ownedKeys = new Set(campaignMonsters.map((m) => m.srdKey).filter((k): k is string => !!k));
  const pageCount = Math.ceil(total / MONSTER_PAGE_SIZE);

  const changeFilter = (next: () => void) => {
    next();
    setPage(0); // เปลี่ยนตัวกรองแล้วต้องกลับหน้าแรก ไม่งั้นค้างอยู่หน้า 5 ของผลลัพธ์ที่มี 2 หน้า = จอว่าง
    setOpenKey(null);
  };

  return (
    <div className="space-y-3">
      <div>
        <h3 className="font-medium">คลังมอนสเตอร์</h3>
        <p className="text-xs opacity-60">
          เลือกจากคลัง SRD แล้วกดเพิ่มเข้าแคมเปญ — หลังจากนั้นวางลงแผนที่ได้จากเครื่องมือแผนที่ด้านบน
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <input
          value={query}
          onChange={(e) => changeFilter(() => setQuery(e.target.value))}
          placeholder="ค้นชื่อ เช่น goblin"
          className={`flex-1 min-w-[10rem] ${inputClass}`}
          aria-label="ค้นหามอนสเตอร์"
        />
        <div className="flex flex-wrap gap-1" role="group" aria-label="ระดับความยาก">
          {CR_FILTERS.map(({ value, label }) => (
            <button
              key={value || "all"}
              type="button"
              onClick={() => changeFilter(() => setCrMax(value))}
              aria-pressed={crMax === value}
              className={`${chipClass} ${
                crMax === value
                  ? "border-indigo-500 bg-indigo-500/15 text-indigo-600 dark:text-indigo-400"
                  : "border-black/20 dark:border-white/20 opacity-60"
              }`}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      {isError && <p className="text-sm text-red-500">โหลดคลังมอนสเตอร์ไม่สำเร็จ</p>}
      {isLoading && <p className="text-sm opacity-50">กำลังโหลดคลัง...</p>}

      {!isLoading && !isError && monsters.length === 0 && (
        <p className="text-sm opacity-60">ไม่พบมอนสเตอร์ที่ตรงกับที่ค้น — ลองคำอื่นหรือขยายระดับความยาก</p>
      )}

      {monsters.length > 0 && (
        <>
          <p className="text-xs opacity-50">
            พบ {total} ตัว · แสดง {page * MONSTER_PAGE_SIZE + 1}–{page * MONSTER_PAGE_SIZE + monsters.length}
          </p>

          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {sortByDifficulty(monsters).map((monster) => (
              <MonsterCard
                key={monster.key}
                monster={monster}
                owned={ownedKeys.has(monster.key)}
                open={openKey === monster.key}
                busy={copyFromSrd.isPending && copyFromSrd.variables === monster.key}
                onToggle={() => setOpenKey(openKey === monster.key ? null : monster.key)}
                onAdd={() => copyFromSrd.mutate(monster.key)}
              />
            ))}
          </div>

          {pageCount > 1 && (
            <div className="flex items-center gap-2 text-sm">
              <button
                type="button"
                onClick={() => setPage((p) => Math.max(0, p - 1))}
                disabled={page === 0}
                className={`${chipClass} border-black/20 dark:border-white/20 disabled:opacity-30`}
              >
                ก่อนหน้า
              </button>
              <span className="text-xs opacity-60 tabular-nums">
                หน้า {page + 1} / {pageCount}
              </span>
              <button
                type="button"
                onClick={() => setPage((p) => (p + 1 < pageCount ? p + 1 : p))}
                disabled={page + 1 >= pageCount}
                className={`${chipClass} border-black/20 dark:border-white/20 disabled:opacity-30`}
              >
                ถัดไป
              </button>
            </div>
          )}
        </>
      )}

      {copyFromSrd.isError && (
        <p className="text-xs text-red-500">เพิ่มเข้าแคมเปญไม่สำเร็จ ลองใหม่อีกครั้ง</p>
      )}

      {campaignMonsters.length > 0 && (
        <div className="space-y-1">
          <p className="text-xs uppercase tracking-wider opacity-50">
            อยู่ในแคมเปญนี้แล้ว ({campaignMonsters.length})
          </p>
          <ul className="flex flex-wrap gap-1 text-xs">
            {campaignMonsters.map((m) => (
              <li
                key={m.id}
                className="rounded border border-black/10 dark:border-white/10 px-2 py-1 opacity-80"
              >
                {m.name}
                <span className="ml-1 tabular-nums opacity-50">
                  AC {m.armorClass} · HP {m.hitPoints}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}

      <SrdAttribution text={attribution} />
    </div>
  );
}

function MonsterCard({
  monster,
  owned,
  open,
  busy,
  onToggle,
  onAdd,
}: {
  monster: SrdMonsterSummary;
  owned: boolean;
  open: boolean;
  busy: boolean;
  onToggle: () => void;
  onAdd: () => void;
}) {
  return (
    <div className={cardClass}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="font-medium truncate">{monster.name}</p>
          <p className="text-xs opacity-60 truncate">{monsterSubtitle(monster)}</p>
        </div>
        <span className={`${chipClass} shrink-0 ${TONE_CLASS[difficultyTone(monster.challengeRating)]}`}>
          {difficultyLabel(monster.challengeRating)}
        </span>
      </div>

      <div className="flex flex-wrap gap-x-3 gap-y-1 text-xs tabular-nums opacity-70">
        <span>AC {monster.armorClass}</span>
        <span>HP {monster.hitPoints}</span>
        <span>เดิน {monster.speedFeet} ฟุต</span>
        <span>เอื้อม {monster.reachFeet} ฟุต</span>
      </div>

      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={onAdd}
          disabled={busy}
          className={`${chipClass} ${
            owned
              ? "border-green-500/40 text-green-700 dark:text-green-400"
              : "border-indigo-500 bg-indigo-500/15 text-indigo-600 dark:text-indigo-400"
          } disabled:opacity-50`}
        >
          {busy ? "กำลังเพิ่ม..." : owned ? "✓ อยู่ในแคมเปญแล้ว" : "+ เพิ่มเข้าแคมเปญ"}
        </button>
        <button
          type="button"
          onClick={onToggle}
          aria-expanded={open}
          className={`${chipClass} border-black/20 dark:border-white/20 opacity-70`}
        >
          {open ? "ซ่อนรายละเอียด" : "ดูท่า/ค่าพลัง"}
        </button>
      </div>

      {open && <MonsterDetail monsterKey={monster.key} />}
    </div>
  );
}

/** stat block เต็ม — โหลดเฉพาะตอนกางออก (คลังมี 330 ตัว ดึงทั้งหมดล่วงหน้าไม่มีเหตุผล) */
function MonsterDetail({ monsterKey }: { monsterKey: string }) {
  const { detail, isLoading, isError } = useSrdMonsterDetail(monsterKey);

  if (isLoading) return <p className="text-xs opacity-50">กำลังโหลด...</p>;
  if (isError) return <p className="text-xs text-red-500">โหลดรายละเอียดไม่สำเร็จ</p>;
  if (!detail) return null;

  return (
    <div className="space-y-2 border-t border-black/10 dark:border-white/10 pt-2">
      <div className="grid grid-cols-6 gap-1 text-center text-[11px]">
        {abilityRows(detail).map((row) => (
          <div key={row.label} className="rounded border border-black/10 dark:border-white/10 py-1">
            <div className="opacity-50">{row.label}</div>
            <div className="font-medium tabular-nums">{row.modifier}</div>
          </div>
        ))}
      </div>

      {detail.data.speedText && (
        <p className="text-[11px] opacity-60">ความเร็ว: {detail.data.speedText}</p>
      )}
      {detail.data.senses && <p className="text-[11px] opacity-60">การรับรู้: {detail.data.senses}</p>}
      {detail.data.immunities && (
        <p className="text-[11px] opacity-60">ภูมิคุ้มกัน: {detail.data.immunities}</p>
      )}

      {monsterActionGroups(detail).map((group) => (
        <div key={group.kind} className="space-y-1">
          <p className="text-[11px] uppercase tracking-wider opacity-50">{group.label}</p>
          {group.entries.map((entry) => (
            <p key={entry.name} className="text-[11px] leading-relaxed">
              <span className="font-medium">{entry.name}.</span> <span className="opacity-70">{entry.text}</span>
            </p>
          ))}
        </div>
      ))}
    </div>
  );
}

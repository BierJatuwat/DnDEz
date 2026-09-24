"use client";

import { useState } from "react";
import {
  DEFAULT_MONSTER_PAGE_SIZE,
  MONSTER_PAGE_SIZE_OPTIONS,
  useCampaignMonsters,
  useSrdMonsterDetail,
  useSrdMonsters,
} from "@/lib/use-srd";
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
import { SrdLanguageToggle } from "./SrdLanguageToggle";
import { useSrdLanguageStore } from "@/lib/srd-language-store";
import {
  STAT_LIMITS,
  draftFromMonster,
  hasChanges,
  hasErrors,
  parseStatDraft,
  type MonsterStatDraft,
  type StatField,
} from "@/lib/monster-stats-form";
import type { MonsterStatChanges } from "@/lib/use-srd";
import { describeSpawned, MAX_SPAWN_COUNT, parseSpawnCount } from "@/lib/monster-spawn";
import type { CampaignMonster, SrdMonsterSummary } from "@/lib/srd-types";

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
  const [pageSize, setPageSize] = useState<number>(DEFAULT_MONSTER_PAGE_SIZE);
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);

  const { monsters, total, attribution, isLoading, isError } = useSrdMonsters({ query, crMax, page, pageSize });
  const { monsters: campaignMonsters, copyFromSrd, updateMonster, spawnMonster } = useCampaignMonsters(campaignId);

  const ownedKeys = new Set(campaignMonsters.map((m) => m.srdKey).filter((k): k is string => !!k));
  const pageCount = Math.ceil(total / pageSize);

  const changeFilter = (next: () => void) => {
    next();
    setPage(0); // เปลี่ยนตัวกรองแล้วต้องกลับหน้าแรก ไม่งั้นค้างอยู่หน้า 5 ของผลลัพธ์ที่มี 2 หน้า = จอว่าง
    setOpenKey(null);
  };

  return (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-2">
        <div>
          <h3 className="font-medium">คลังมอนสเตอร์</h3>
          <p className="text-xs opacity-60">
            เลือกจากคลัง SRD แล้วกดเพิ่มเข้าแคมเปญ — หลังจากนั้นวางลงแผนที่ได้จากเครื่องมือแผนที่ด้านบน
          </p>
        </div>
        <SrdLanguageToggle />
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
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-xs opacity-50">
              พบ {total} ตัว · แสดง {page * pageSize + 1}–{page * pageSize + monsters.length}
            </p>
            <label className="flex items-center gap-1 text-xs opacity-70">
              แสดงทีละ
              <select
                value={pageSize}
                onChange={(e) => changeFilter(() => setPageSize(Number(e.target.value)))}
                className={`${inputClass} py-0.5`}
              >
                {MONSTER_PAGE_SIZE_OPTIONS.map((size) => (
                  <option key={size} value={size}>
                    {size} ตัว
                  </option>
                ))}
              </select>
            </label>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {sortByDifficulty(monsters).map((monster) => (
              <MonsterCard
                key={monster.key}
                monster={monster}
                owned={ownedKeys.has(monster.key)}
                open={openKey === monster.key}
                busy={copyFromSrd.isPending && copyFromSrd.variables === monster.key}
                onToggle={() => setOpenKey(openKey === monster.key ? null : monster.key)}
                onAdd={() =>
                  copyFromSrd.mutate(monster.key, {
                    // เพิ่งเพิ่มเข้ามา = จังหวะที่ DM อยากปรับค่าพอดี เปิดฟอร์มรอไว้เลยแทนให้ไปหาเอง
                    onSuccess: (result) => setEditingId(result.monsterId),
                  })
                }
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
            อยู่ในแคมเปญนี้แล้ว ({campaignMonsters.length}) — กดเพื่อส่งลงสนามหรือปรับค่าพลัง
          </p>
          <ul className="space-y-1">
            {campaignMonsters.map((m) => (
              <li key={m.id} className="rounded border border-black/10 dark:border-white/10">
                <button
                  type="button"
                  onClick={() => setEditingId(editingId === m.id ? null : m.id)}
                  aria-expanded={editingId === m.id}
                  className="w-full flex items-center justify-between gap-2 px-2 py-1 text-left text-xs hover:bg-black/5 dark:hover:bg-white/5"
                >
                  <span className="truncate">{m.name}</span>
                  <span className="shrink-0 tabular-nums opacity-50">
                    AC {m.armorClass} · HP {m.hitPoints} · เดิน {m.speedFeet} · เอื้อม {m.reachFeet}
                  </span>
                </button>
                {editingId === m.id && (
                  <SpawnControls monster={m} spawn={spawnMonster} />
                )}
                {editingId === m.id && (
                  <MonsterStatEditor
                    monster={m}
                    onSave={(changes) =>
                      updateMonster.mutate(
                        { monsterId: m.id, ...changes },
                        { onSuccess: () => setEditingId(null) },
                      )
                    }
                    saving={updateMonster.isPending}
                    failed={updateMonster.isError}
                  />
                )}
              </li>
            ))}
          </ul>
        </div>
      )}

      <SrdAttribution text={attribution} />
    </div>
  );
}

/**
 * ส่งมอนสเตอร์ลงสนามเป็นตัวที่มี HP ของตัวเอง — ต่างจากการวาง token แม่แบบตรง ๆ (ใช้ค่าชุดเดียวกันทุกตัว)
 * ตัวที่ spawn เข้าวง initiative ได้และถูกลบพร้อมสรุปผลลงบันทึกแคมเปญตอนจบ encounter
 */
function SpawnControls({
  monster,
  spawn,
}: {
  monster: CampaignMonster;
  spawn: ReturnType<typeof useCampaignMonsters>["spawnMonster"];
}) {
  const [countText, setCountText] = useState("1");
  const [statsVisible, setStatsVisible] = useState(false);
  const [lastSpawned, setLastSpawned] = useState<string | null>(null);
  const count = parseSpawnCount(countText);
  const isMine = spawn.variables?.monsterId === monster.id;

  return (
    <div className="space-y-1 border-t border-black/10 dark:border-white/10 px-2 py-2">
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <label className="flex items-center gap-1">
          จำนวน
          <input
            value={countText}
            onChange={(e) => setCountText(e.target.value)}
            inputMode="numeric"
            aria-label={`จำนวน ${monster.name} ที่จะส่งลงสนาม`}
            aria-invalid={count === null}
            className={`${inputClass} w-14 tabular-nums`}
          />
        </label>
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={statsVisible} onChange={(e) => setStatsVisible(e.target.checked)} />
          ผู้เล่นเห็น HP/AC
        </label>
        <button
          type="button"
          disabled={count === null || (spawn.isPending && isMine)}
          onClick={() =>
            count !== null &&
            spawn.mutate(
              { monsterId: monster.id, count, statsVisibleToParty: statsVisible },
              { onSuccess: (spawned) => setLastSpawned(describeSpawned(spawned.map((s) => s.name))) },
            )
          }
          className="rounded bg-rose-700 px-2 py-1 text-white disabled:opacity-50"
        >
          ส่งลงสนาม
        </button>
      </div>
      {count === null && (
        <p className="text-[11px] text-red-500">ใส่จำนวน 1–{MAX_SPAWN_COUNT}</p>
      )}
      {lastSpawned && !(spawn.isError && isMine) && (
        <p className="text-[11px] opacity-70">
          ส่ง {lastSpawned} ลงแล้ว — เลือกเข้าวงต่อสู้ได้ที่แผง Encounter · จบ encounter แล้วจะถูกลบพร้อมสรุปผล
        </p>
      )}
      {spawn.isError && isMine && <p className="text-[11px] text-red-500">ส่งลงสนามไม่สำเร็จ ลองใหม่อีกครั้ง</p>}
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
  // ต้องเรียกก่อนทุก early return — hook ห้ามถูกข้ามแบบมีเงื่อนไขระหว่าง render (rules-of-hooks)
  const lang = useSrdLanguageStore((s) => s.language);

  if (isLoading) return <p className="text-xs opacity-50">กำลังโหลด...</p>;
  if (isError) return <p className="text-xs text-red-500">โหลดรายละเอียดไม่สำเร็จ</p>;
  if (!detail) return null;

  return (
    <div className="space-y-2 border-t border-black/10 dark:border-white/10 pt-2">
      {/* ชื่อมอนสเตอร์เป็น "ชื่อเฉพาะ" ไม่แปล (คงอังกฤษเสมอ) — ขนาด/ชนิด/แนวคิด/ค่าตัวเลขแปลแล้วเสมอ แต่
          traits/actions เป็น "ชุดที่ใช้จริงก่อน" เท่านั้น — บอกตรง ๆ ว่าเนื้อหาด้านล่างเป็นอังกฤษ ไม่ใช่ปล่อย
          ให้เห็นภาษาอังกฤษแทรกกลางหน้าไทยแล้วงงว่าทำไม */}
      {lang === "th" && detail.contentTranslated === false && (
        <p className="text-[11px] rounded bg-amber-500/10 text-amber-700 dark:text-amber-400 px-2 py-1">
          ท่า/คุณสมบัติของตัวนี้ยังไม่มีคำแปล — แสดงเป็นภาษาอังกฤษ
        </p>
      )}

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

/**
 * ฟอร์มปรับค่าพลังของแม่แบบมอนสเตอร์
 *
 * **บอกให้ชัดว่าแก้แล้วกระทบอะไร** — ค่านี้เป็นของ "แม่แบบ" ก็อบลินทุกตัวที่วางจากแม่แบบเดียวกันใช้ร่วมกัน
 * DM ที่คิดว่ากำลังแก้ตัวใดตัวหนึ่งบนแผนที่จะงงมากถ้าตัวอื่นเปลี่ยนตามโดยไม่มีใครบอก (จะแยกรายตัวได้
 * ต้องมี HP รายตัวก่อน ซึ่งยังไม่มี)
 *
 * ปุ่มบันทึกปิดเมื่อ **ไม่มีอะไรเปลี่ยน** ด้วย ไม่ใช่แค่ตอนค่าผิด — กดบันทึกทั้งที่ไม่ได้แก้อะไรแล้วเห็น
 * "สำเร็จ" ทำให้ไม่แน่ใจว่าที่แก้ไปก่อนหน้าบันทึกหรือยัง
 */
function MonsterStatEditor({
  monster,
  onSave,
  saving,
  failed,
}: {
  monster: CampaignMonster;
  onSave: (changes: MonsterStatChanges) => void;
  saving: boolean;
  failed: boolean;
}) {
  const [draft, setDraft] = useState<MonsterStatDraft>(() => draftFromMonster(monster));
  const result = parseStatDraft(draft, monster);
  const blocked = hasErrors(result) || !hasChanges(result) || saving;

  const field = (key: StatField, label: string, hint?: string) => (
    <label className="flex flex-col gap-0.5">
      <span className="opacity-60">{label}</span>
      <input
        type="number"
        inputMode="numeric"
        value={draft[key]}
        min={STAT_LIMITS[key].min}
        max={STAT_LIMITS[key].max}
        onChange={(e) => setDraft({ ...draft, [key]: e.target.value })}
        aria-label={label}
        className={`w-full rounded border bg-transparent px-2 py-1 tabular-nums ${
          result.errors[key] ? "border-red-500" : "border-black/20 dark:border-white/20"
        }`}
      />
      {result.errors[key] ? (
        <span className="text-red-500">{result.errors[key]}</span>
      ) : hint ? (
        <span className="opacity-40">{hint}</span>
      ) : null}
    </label>
  );

  return (
    <div className="space-y-2 border-t border-black/10 dark:border-white/10 p-2 text-[11px]">
      <label className="flex flex-col gap-0.5">
        <span className="opacity-60">ชื่อ</span>
        <input
          value={draft.name}
          onChange={(e) => setDraft({ ...draft, name: e.target.value })}
          aria-label="ชื่อมอนสเตอร์"
          className={`w-full rounded border bg-transparent px-2 py-1 ${
            result.errors.name ? "border-red-500" : "border-black/20 dark:border-white/20"
          }`}
        />
        {result.errors.name && <span className="text-red-500">{result.errors.name}</span>}
      </label>

      <div className="grid grid-cols-2 gap-2">
        {field("armorClass", "ค่าเกราะ (AC)", "ยิ่งสูงยิ่งตียาก")}
        {field("hitPoints", "พลังชีวิต (HP)")}
        {field("speedFeet", "ระยะเดิน (ฟุต)", "30 = ปกติ · 0 = ขยับไม่ได้")}
        {field("reachFeet", "ระยะเอื้อม (ฟุต)", "10 ขึ้นไป = สวนได้จากไกลขึ้น")}
      </div>

      <p className="opacity-50">
        ⚠️ ค่านี้เป็นของแม่แบบ — ถ้าวางมอนสเตอร์ตัวนี้บนแผนที่ไว้หลายตัว จะเปลี่ยนพร้อมกันทุกตัว
      </p>

      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => onSave(result.changes)}
          disabled={blocked}
          className={`${chipClass} border-indigo-500 bg-indigo-500/15 text-indigo-600 dark:text-indigo-400 disabled:opacity-40`}
        >
          {saving ? "กำลังบันทึก..." : "บันทึก"}
        </button>
        <button
          type="button"
          onClick={() => setDraft(draftFromMonster(monster))}
          disabled={saving || !hasChanges(result)}
          className={`${chipClass} border-black/20 dark:border-white/20 opacity-70 disabled:opacity-30`}
        >
          คืนค่าเดิม
        </button>
      </div>

      {failed && <p className="text-red-500">บันทึกไม่สำเร็จ ลองใหม่อีกครั้ง</p>}
    </div>
  );
}

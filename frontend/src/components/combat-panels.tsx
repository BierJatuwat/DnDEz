"use client";

import { useState } from "react";
import type { CharacterSummary } from "@/lib/campaign-types";
import {
  COMMON_DAMAGE_TYPES,
  TURN_RESOURCES,
  roundOf,
  type Encounter,
} from "@/lib/combat-types";
import type { useCombatActions } from "@/lib/use-combat-actions";

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";
const inputClass =
  "rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5";
const primaryBtn = "rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50";

type Combat = ReturnType<typeof useCombatActions>;

/**
 * defect รอบ B (Layer 1) — ภาษาการแสดงผลร่วมของหน้าต่อสู้
 *
 * ก่อนหน้านี้ทุกอย่างเป็น "ช่องกรอก + ข้อความดิบ" ข้อมูลที่ระบบรู้อยู่แล้ว (เทิร์นของใคร, HP เท่าไร,
 * ผลทอยล่าสุด) ถูกเขียนลง log บรรทัดเดียวแล้วจบ · ชุด primitive ข้างล่างทำให้ทุกพาเนลพูดภาษา
 * เดียวกัน: label ตัวเล็กจาง, ค่าตัวเลขชิดขวาแบบ tabular, สถานะเป็น badge สี ไม่ใช่ข้อความ [Rolling]
 */

const BADGE_TONES = {
  neutral: "bg-black/10 dark:bg-white/10 opacity-80",
  active: "bg-amber-500/20 text-amber-600 dark:text-amber-400",
  good: "bg-green-500/15 text-green-600 dark:text-green-400",
  warn: "bg-yellow-500/15 text-yellow-600 dark:text-yellow-400",
  danger: "bg-red-500/15 text-red-600 dark:text-red-400",
} as const;

function Badge({
  children,
  tone = "neutral",
}: {
  children: React.ReactNode;
  tone?: keyof typeof BADGE_TONES;
}) {
  return (
    <span
      className={`inline-block shrink-0 rounded px-2 py-0.5 text-xs font-medium ${BADGE_TONES[tone]}`}
    >
      {children}
    </span>
  );
}

/** หัวข้อพาเนลแบบในรูป — ตัวเล็ก จาง เว้นระยะตัวอักษร ไม่แย่งสายตาไปจากข้อมูล */
function PanelLabel({ children }: { children: React.ReactNode }) {
  return (
    <h2 className="text-xs uppercase tracking-wider opacity-50">{children}</h2>
  );
}

/**
 * Sprint F งานที่ 6b — UI ของ encounter/turn order/death save/attack/check
 *
 * backend มีครบตั้งแต่งานที่ 4-5 แต่ไม่เคยมีหน้าจอ · ทุก panel ที่นี่ใช้ร่วมกันทั้งหน้า DM และหน้า
 * ผู้เล่น ต่างกันที่ prop `canControlEncounter` (DM เท่านั้นที่เปิด/เดินเทิร์น/จบ encounter ได้)
 *
 * ⚠️ การซ่อนปุ่มเป็นเรื่อง UX — สิทธิ์จริงบังคับที่ API ทุกจุด (403/409 ถ้าเรียกผิดสิทธิ์/ผิดจังหวะ)
 */

export function EncounterPanel({
  combat,
  characters,
  canControlEncounter,
}: {
  combat: Combat;
  characters: CharacterSummary[];
  canControlEncounter: boolean;
}) {
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const encounter: Encounter | null = combat.encounter;

  const toggle = (id: string) =>
    setPicked((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  if (!encounter) {
    return (
      <section className={sectionClass}>
        <h2 className="font-medium">Encounter</h2>
        {!canControlEncounter ? (
          <p className="text-sm opacity-50">ยังไม่มีการต่อสู้ — รอ DM เปิด encounter</p>
        ) : (
          <>
            <p className="text-xs opacity-60">
              เลือกตัวละครที่ร่วมต่อสู้ (ระบบทอย initiative ให้เอง — สูงสุด 20 ตัว)
            </p>
            <ul className="space-y-1">
              {characters.map((c) => (
                <li key={c.id}>
                  <label className="flex items-center gap-2 text-sm px-1 py-1">
                    <input
                      type="checkbox"
                      checked={picked.has(c.id)}
                      onChange={() => toggle(c.id)}
                    />
                    {c.name}
                    {c.controlledByDm && (
                      <span className="text-xs text-indigo-500">[NPC]</span>
                    )}
                  </label>
                </li>
              ))}
              {characters.length === 0 && (
                <li className="opacity-50 text-sm">ยังไม่มีตัวละครให้เลือก</li>
              )}
            </ul>
            <button
              onClick={() => combat.startEncounter.mutate([...picked])}
              disabled={combat.startEncounter.isPending || picked.size === 0}
              className={primaryBtn}
            >
              เปิด encounter ({picked.size} ตัว)
            </button>
          </>
        )}
      </section>
    );
  }

  const current = encounter.participants.find((p) => p.id === encounter.currentParticipantId);

  return (
    <section className={sectionClass}>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs uppercase tracking-wider opacity-50">
            รอบที่ {roundOf(encounter)} · เทิร์นที่ {encounter.turnNumber + 1}
          </p>
          <h2 className="text-lg font-semibold">การต่อสู้</h2>
        </div>
        {current && <Badge tone="active">เทิร์นของ {current.characterName}</Badge>}
      </div>

      {canControlEncounter && (
        <div className="flex gap-2">
          <button
            onClick={() => combat.nextTurn.mutate(encounter.id)}
            disabled={combat.nextTurn.isPending}
            className={primaryBtn}
          >
            เทิร์นถัดไป
          </button>
          <button
            onClick={() => combat.endEncounter.mutate(encounter.id)}
            disabled={combat.endEncounter.isPending}
            className="rounded bg-red-600/80 px-3 py-1.5 text-white disabled:opacity-50"
          >
            จบ
          </button>
        </div>
      )}

      <div className="space-y-1">
        <PanelLabel>ลำดับการเล่น</PanelLabel>
        <ol>
          {encounter.participants.map((p) => {
            const isCurrent = p.id === encounter.currentParticipantId;
            return (
              <li
                key={p.id}
                /* แถบซ้ายบอกเทิร์นปัจจุบันแทนสัญลักษณ์ ▶ — อ่านเป็นตำแหน่งได้ทันทีโดยไม่ต้องกวาดหา */
                className={`flex items-center justify-between gap-2 border-l-2 py-1.5 pl-3 pr-1 text-sm ${
                  isCurrent
                    ? "border-amber-500 bg-amber-500/10 font-medium"
                    : "border-transparent"
                }`}
              >
                <span className="truncate">
                  <span className="opacity-40 mr-1.5">{p.turnOrder + 1}</span>
                  {p.characterName}
                </span>
                {/* รายละเอียดการทอยย้ายไป title — ในรูปโชว์แค่ยอดรวม ไม่ให้เลขรกสายตา */}
                <span
                  className="tabular-nums opacity-60"
                  title={`ทอย ${p.initiativeRoll}${p.initiativeModifier >= 0 ? "+" : ""}${p.initiativeModifier}`}
                >
                  {p.initiativeTotal}
                </span>
              </li>
            );
          })}
        </ol>
      </div>
    </section>
  );
}

/**
 * พาเนล "พลังชีวิต" ตามรูปที่ 3 — ข้อมูลนี้มีมาตั้งแต่ Sprint E Part 3 แล้ว แต่เดิมถูกแสดงปนอยู่ใน
 * ข้อความของ roster (`ชื่อ (level 1) 24/28 HP`) ทำให้ตอนสู้จริงต้องกวาดตาหา · แยกออกมาเป็นพาเนล
 * ของตัวเองพร้อมสีตามสัดส่วน HP เพื่อให้เห็นได้ทันทีว่าใครใกล้ตาย
 */
export function VitalsPanel({
  combat,
  characters,
}: {
  combat: Combat;
  characters: CharacterSummary[];
}) {
  // ตัวที่ HP ถูกซ่อน (null) ไม่มีอะไรให้แสดง — ตัดออกแทนที่จะโชว์ "?" ให้รก (งานที่ 7)
  const visible = characters.filter((c) => c.currentHp !== null && c.maxHp !== null);
  if (visible.length === 0) return null;

  return (
    <section className={sectionClass}>
      <PanelLabel>พลังชีวิต</PanelLabel>
      <ul className="space-y-1">
        {visible.map((c) => {
          const hp = c.currentHp!;
          const max = c.maxHp!;
          const ratio = max > 0 ? hp / max : 0;
          const tone =
            hp <= 0
              ? "text-red-500"
              : ratio <= 0.35
                ? "text-yellow-500"
                : "text-green-600 dark:text-green-400";
          return (
            <li key={c.id} className="flex items-center justify-between gap-2 py-1 text-sm">
              <span className="flex min-w-0 items-center gap-2">
                <span className="truncate">{c.name}</span>
                {hp <= 0 && <DeathSaveBadge combat={combat} characterId={c.id} />}
              </span>
              <span className={`tabular-nums font-medium ${tone}`}>
                {hp}/{max}
                {c.currentTempHp ? (
                  <span className="ml-1 text-xs opacity-60">+{c.currentTempHp}</span>
                ) : null}
              </span>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

/**
 * แถบ "ล่าสุด" ตามรูปที่ 3 — เดิมผลทอยจมอยู่ในกล่อง Log รวมกับข้อความ debug ของ SignalR
 * ทำให้สิ่งที่เพิ่งเกิดขึ้นในเกม (ซึ่งคือสิ่งที่คนทั้งโต๊ะรอดู) ไม่ต่างอะไรกับ log ของระบบ
 */
export function LatestEventBar({
  combat,
  characters,
}: {
  combat: Combat;
  characters: CharacterSummary[];
}) {
  const event = combat.latestEvent;
  if (!event) return null;

  const nameOf = (id: string) => characters.find((c) => c.id === id)?.name ?? "ใครบางคน";

  let body: React.ReactNode;
  if (event.kind === "attack") {
    body = (
      <>
        <span className="font-medium">{nameOf(event.attackerId)}</span> โจมตี{" "}
        <span className="font-medium">{nameOf(event.targetId)}</span>{" "}
        <span className="opacity-60">ทอย</span>{" "}
        <span className="tabular-nums font-medium text-amber-500">{event.total}</span>
        {" · "}
        {event.hit ? (
          <>
            <span className="text-green-600 dark:text-green-400">เข้า</span>
            {event.damage !== null && (
              <>
                {" · "}
                <span className="tabular-nums font-medium">{event.damage}</span>{" "}
                <span className="opacity-60">หน่วย</span>
              </>
            )}
          </>
        ) : (
          <span className="opacity-60">พลาด</span>
        )}
      </>
    );
  } else if (event.kind === "check") {
    body = (
      <>
        <span className="font-medium">{nameOf(event.characterId)}</span> ทอยเช็ค{" "}
        <span className="tabular-nums font-medium text-amber-500">{event.total}</span>{" "}
        <span className="opacity-60">vs DC {event.dc}</span>
        {" · "}
        {event.success ? (
          <span className="text-green-600 dark:text-green-400">ผ่าน</span>
        ) : (
          <span className="opacity-60">ไม่ผ่าน</span>
        )}
      </>
    );
  } else {
    body = (
      <>
        <span className="font-medium">{nameOf(event.characterId)}</span> ทอย death save{" "}
        <span className="opacity-60">{event.outcome}</span>
        {" · "}
        <span className="tabular-nums">
          สำเร็จ {event.successes}/3 · ล้มเหลว {event.failures}/3
        </span>
      </>
    );
  }

  return (
    <section className="rounded border border-black/10 dark:border-white/10 px-4 py-3 flex items-baseline gap-3">
      <span className="shrink-0 text-xs uppercase tracking-wider opacity-50">ล่าสุด</span>
      <p className="text-sm min-w-0">{body}</p>
    </section>
  );
}

/** สถานะ death save เป็น badge — แยกเป็นคอมโพเนนต์เพราะต้อง query ต่อตัวละคร (rules of hooks) */
function DeathSaveBadge({ combat, characterId }: { combat: Combat; characterId: string }) {
  const { data, isError } = combat.useDeathSave(characterId);
  // 403 = แคมเปญตั้ง DeathSaveVisibility = DmOnly ไว้ ไม่ใช่บั๊ก จึงเงียบไป (เหมือน DeathSaveRow)
  if (isError || !data || data.status === "NotDying") return null;

  if (data.status === "Dead") return <Badge tone="danger">ตาย</Badge>;
  if (data.status === "Stable") return <Badge tone="good">ทรงตัว</Badge>;
  return (
    <Badge tone="warn">
      ล้มลง {data.successes}/3 · {data.failures}/3
    </Badge>
  );
}

export function TurnStatePanel({ combat }: { combat: Combat }) {
  const [feet, setFeet] = useState("5");
  const encounter = combat.encounter;
  if (!encounter || !encounter.turnState) return null;

  const ts = encounter.turnState;
  const current = encounter.participants.find(
    (p) => p.id === encounter.currentParticipantId,
  );

  return (
    <section className={sectionClass}>
      <h2 className="font-medium">
        ทรัพยากรเทิร์นนี้{current ? ` — ${current.characterName}` : ""}
      </h2>
      <ul className="flex flex-wrap gap-3 text-sm">
        <li className={ts.actionAvailable ? "text-green-500" : "opacity-40 line-through"}>
          Action
        </li>
        <li className={ts.bonusActionAvailable ? "text-green-500" : "opacity-40 line-through"}>
          Bonus
        </li>
        <li className={ts.reactionAvailable ? "text-green-500" : "opacity-40 line-through"}>
          Reaction
        </li>
        <li className="opacity-70">
          Movement {ts.movementRemainingFeet}/{ts.maxMovementFeet} ฟุต
        </li>
      </ul>
      {/* ปุ่มโชว์ให้ทุกคนเห็น — API เป็นคนตัดสินว่าใครกดได้จริง (DM หรือเจ้าของตัวละครที่ถึงตา)
          ถ้ากดผิดจังหวะจะได้ 403/400 กลับมาแล้วขึ้นใน log */}
      <div className="flex flex-wrap items-center gap-2">
        {TURN_RESOURCES.filter((r) => r.value !== "Movement").map((r) => (
          <button
            key={r.value}
            onClick={() =>
              combat.spendTurnResource.mutate({
                encounterId: encounter.id,
                resource: r.value,
              })
            }
            disabled={combat.spendTurnResource.isPending}
            className="rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-50"
          >
            ใช้ {r.label}
          </button>
        ))}
        <input
          value={feet}
          onChange={(e) => setFeet(e.target.value)}
          className={`w-16 text-xs ${inputClass}`}
          aria-label="ระยะที่เคลื่อนที่ (ฟุต)"
        />
        <button
          onClick={() =>
            combat.spendTurnResource.mutate({
              encounterId: encounter.id,
              resource: "Movement",
              feet: Number(feet) || 0,
            })
          }
          disabled={combat.spendTurnResource.isPending}
          className="rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-50"
        >
          เคลื่อนที่
        </button>
      </div>
    </section>
  );
}

/**
 * แถว death save ของตัวละครหนึ่งตัว — แยกเป็นคอมโพเนนต์เพราะแต่ละตัวต้องเรียก query ของตัวเอง
 * (เรียก hook ใน loop ไม่ได้ตาม rules of hooks)
 */
function DeathSaveRow({
  combat,
  character,
  isDm,
  canRoll,
}: {
  combat: Combat;
  character: CharacterSummary;
  isDm: boolean;
  canRoll: boolean;
}) {
  const query = combat.useDeathSave(character.id);
  const state = query.data;

  // DmOnly visibility → ผู้เล่นได้ 403 ที่ endpoint นี้ ไม่ใช่บั๊ก (DM ตั้งค่าไว้เอง) จึงเงียบไป
  if (query.isError) return null;
  if (!state) return null;

  return (
    <li className="flex items-center justify-between gap-2 px-3 py-1.5 text-sm">
      <span>
        {character.name}{" "}
        <span
          className={
            state.status === "Dead"
              ? "text-red-500 text-xs"
              : state.status === "Stable"
                ? "text-green-500 text-xs"
                : state.status === "Rolling"
                  ? "text-yellow-500 text-xs"
                  : "opacity-50 text-xs"
          }
        >
          [{state.status}]
        </span>{" "}
        {state.status !== "NotDying" && (
          <span className="text-xs opacity-70">
            สำเร็จ {state.successes}/3 · ล้มเหลว {state.failures}/3
          </span>
        )}
      </span>
      <span className="flex gap-2">
        {isDm && state.status === "NotDying" && (
          <button
            onClick={() => combat.confirmDying.mutate(character.id)}
            disabled={combat.confirmDying.isPending}
            className="rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-50"
          >
            ยืนยันเข้า dying
          </button>
        )}
        {canRoll && state.status === "Rolling" && (
          <button
            onClick={() => combat.rollDeathSave.mutate(character.id)}
            disabled={combat.rollDeathSave.isPending}
            className={primaryBtn}
          >
            ทอย death save
          </button>
        )}
      </span>
    </li>
  );
}

export function DeathSavePanel({
  combat,
  characters,
  isDm,
  userId,
}: {
  combat: Combat;
  characters: CharacterSummary[];
  isDm: boolean;
  userId: string | null;
}) {
  // แสดงเฉพาะตัวที่ HP ถึง 0 แล้ว (ตัวที่ HP ถูกซ่อน = null จะไม่เข้าเงื่อนไข — DM ที่เห็นค่าจริง
  // เท่านั้นที่จะเห็น NPC โผล่ในลิสต์นี้ ซึ่งตรงกับเจตนาการซ่อน stat ของงานที่ 7)
  const dying = characters.filter((c) => c.currentHp === 0);
  if (dying.length === 0) return null;

  return (
    <section className={sectionClass}>
      <h2 className="font-medium">Death save (ตัวละครที่ 0 HP)</h2>
      <p className="text-xs opacity-60">
        DM ต้องกดยืนยันก่อนตัวละครถึงจะเริ่มทอย death save ได้ (ไม่เข้าอัตโนมัติตอน HP หมด)
      </p>
      <ul className="space-y-1">
        {dying.map((c) => (
          <DeathSaveRow
            key={c.id}
            combat={combat}
            character={c}
            isDm={isDm}
            canRoll={isDm || c.ownerUserId === userId}
          />
        ))}
      </ul>
    </section>
  );
}

export function AttackCheckPanel({
  combat,
  characters,
  actableCharacters,
  showAttack = true,
}: {
  combat: Combat;
  characters: CharacterSummary[];
  /** ตัวละครที่ผู้ใช้คนนี้สั่งได้จริง (ของตัวเอง หรือ NPC ที่ DM คุม) */
  actableCharacters: CharacterSummary[];
  /**
   * Sprint G Part 9 — หน้าผู้เล่นปิดส่วน "โจมตี" ทิ้ง เพราะย้ายไปใช้ `AffordancePanel` ที่ server
   * คำนวณเป้าและข้อจำกัดมาให้แล้ว (เลือกจากรายการ ไม่ใช่เลือกเป้าจาก dropdown ของทุกตัวละคร
   * ในแคมเปญแล้วค่อยรู้ทีหลังว่าเอื้อมไม่ถึง) · หน้า DM ยังใช้ทางเดิมอยู่ เพราะ affordance รอบนี้
   * ทำฝั่งผู้เล่นก่อน
   */
  showAttack?: boolean;
}) {
  const [attacker, setAttacker] = useState("");
  const [target, setTarget] = useState("");
  const [attackExpr, setAttackExpr] = useState("1d20+5");
  const [damageExpr, setDamageExpr] = useState("1d8+3");
  const [damageType, setDamageType] = useState<string>(COMMON_DAMAGE_TYPES[0]);
  const [checkExpr, setCheckExpr] = useState("1d20+3");
  const [dc, setDc] = useState("12");

  if (actableCharacters.length === 0) return null;

  const attackerId = attacker || actableCharacters[0].id;
  const targetId = target || characters.find((c) => c.id !== attackerId)?.id || "";

  return (
    <section className={sectionClass}>
      <h2 className="font-medium">{showAttack ? "โจมตี / ทอยเช็ค" : "ทอยเช็ค"}</h2>

      {showAttack && (
      <div className="space-y-2">
        <p className="text-xs opacity-60">โจมตี</p>
        <div className="flex flex-wrap gap-2">
          <select
            value={attackerId}
            onChange={(e) => setAttacker(e.target.value)}
            className={`text-sm ${inputClass}`}
            aria-label="ตัวละครผู้โจมตี"
          >
            {actableCharacters.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
          <select
            value={targetId}
            onChange={(e) => setTarget(e.target.value)}
            className={`text-sm ${inputClass}`}
            aria-label="เป้าหมาย"
          >
            {characters
              .filter((c) => c.id !== attackerId)
              .map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
          </select>
        </div>
        <div className="flex flex-wrap gap-2">
          <input
            value={attackExpr}
            onChange={(e) => setAttackExpr(e.target.value)}
            className={`w-28 text-sm ${inputClass}`}
            aria-label="สูตรทอยโจมตี"
          />
          <input
            value={damageExpr}
            onChange={(e) => setDamageExpr(e.target.value)}
            className={`w-28 text-sm ${inputClass}`}
            aria-label="สูตรดาเมจ"
          />
          <select
            value={damageType}
            onChange={(e) => setDamageType(e.target.value)}
            className={`text-sm ${inputClass}`}
            aria-label="ชนิดดาเมจ"
          >
            {COMMON_DAMAGE_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
          <button
            onClick={() =>
              combat.makeAttack.mutate({
                attackerId,
                targetCharacterId: targetId,
                expression: attackExpr,
                damageExpression: damageExpr,
                damageType,
              })
            }
            disabled={combat.makeAttack.isPending || !targetId}
            className={primaryBtn}
          >
            โจมตี
          </button>
        </div>
      </div>
      )}

      <div className={showAttack ? "space-y-2 border-t border-black/10 dark:border-white/10 pt-3" : "space-y-2"}>
        <p className="text-xs opacity-60">ทอยเช็ค (vs DC)</p>
        <div className="flex flex-wrap gap-2">
          <input
            value={checkExpr}
            onChange={(e) => setCheckExpr(e.target.value)}
            className={`w-28 text-sm ${inputClass}`}
            aria-label="สูตรทอยเช็ค"
          />
          <input
            value={dc}
            onChange={(e) => setDc(e.target.value)}
            className={`w-16 text-sm ${inputClass}`}
            aria-label="DC"
          />
          <button
            onClick={() =>
              combat.makeCheck.mutate({
                characterId: attackerId,
                expression: checkExpr,
                dc: Number(dc) || 10,
              })
            }
            disabled={combat.makeCheck.isPending}
            className={primaryBtn}
          >
            ทอยเช็ค
          </button>
        </div>
      </div>
    </section>
  );
}

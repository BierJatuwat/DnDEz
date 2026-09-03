"use client";

import { useState } from "react";
import type { CharacterSummary } from "@/lib/campaign-types";
import { COMMON_DAMAGE_TYPES, TURN_RESOURCES, type Encounter } from "@/lib/combat-types";
import type { useCombatActions } from "@/lib/use-combat-actions";

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";
const inputClass =
  "rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5";
const primaryBtn = "rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50";

type Combat = ReturnType<typeof useCombatActions>;

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

  return (
    <section className={sectionClass}>
      <div className="flex items-center justify-between">
        <h2 className="font-medium">Encounter — ลำดับเทิร์น</h2>
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
      </div>
      <ol className="space-y-1">
        {encounter.participants.map((p) => {
          const isCurrent = p.id === encounter.currentParticipantId;
          return (
            <li
              key={p.id}
              className={`flex items-center justify-between rounded px-3 py-1.5 text-sm ${
                isCurrent ? "bg-indigo-600/20 font-medium" : ""
              }`}
            >
              <span>
                {isCurrent && <span className="mr-1">▶</span>}
                {p.turnOrder + 1}. {p.characterName}
              </span>
              <span className="opacity-60 text-xs">
                initiative {p.initiativeTotal} ({p.initiativeRoll}
                {p.initiativeModifier >= 0 ? "+" : ""}
                {p.initiativeModifier})
              </span>
            </li>
          );
        })}
      </ol>
    </section>
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
}: {
  combat: Combat;
  characters: CharacterSummary[];
  /** ตัวละครที่ผู้ใช้คนนี้สั่งได้จริง (ของตัวเอง หรือ NPC ที่ DM คุม) */
  actableCharacters: CharacterSummary[];
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
      <h2 className="font-medium">โจมตี / ทอยเช็ค</h2>

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

      <div className="space-y-2 border-t border-black/10 dark:border-white/10 pt-3">
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

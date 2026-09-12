"use client";

import { useState } from "react";
import { useAffordances } from "@/lib/use-affordances";
import {
  advantageBadge,
  costLabel,
  groupByOption,
  spentResourceLabels,
  targetLine,
} from "@/lib/affordance-view";
import { attackPlan, damageTypeLabel, describeDice, formulaLine } from "@/lib/affordance-formula";
import type { AffordanceItem } from "@/lib/affordance-types";
import type { useCombatActions } from "@/lib/use-combat-actions";

/** แพทเทิร์นเดียวกับ combat-panels.tsx — ผูกกับ return type ของ hook ตรง ๆ ไม่ประกาศ interface ซ้ำ */
type Combat = ReturnType<typeof useCombatActions>;

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";

/**
 * Sprint G Part 9 — พาเนล "คุณจะทำอะไร?" (mockup `docs/mockups/uxpilot-player-view.png`)
 *
 * แทนที่ฟอร์มเดิมที่ให้ผู้เล่นเลือกเป้าจาก dropdown ของ **ทุกตัวละครในแคมเปญ** แล้วกรอกสูตรเต๋าเอง
 * ด้วยรายการที่ server คำนวณมาแล้ว — ตาม §1.1 *"พาเนลข้างไม่ใช่ฟอร์มให้เลือกว่าจะโจมตีใคร แต่เป็น
 * ผลลัพธ์ที่คำนวณมาแล้ว — ศัตรูที่อยู่ไกลเกินไม่ปรากฏในลิสต์ตั้งแต่แรก ไม่ใช่ให้เลือกแล้วค่อยเด้ง error"*
 *
 * สี่สถานะของการ์ดลอกจาก mockup:
 *   1. ปกติ — ชื่อท่า + ราคา ไม่มีป้าย
 *   2. DISADVANTAGE — ป้ายแดง + กล่องเหตุผลขอบแดง
 *   3. ADVANTAGE — ป้ายเขียว + กล่องเหตุผลขอบเขียว
 *   4. ใช้ไปแล้ว — เทาจาง (มาจาก turn state ดูเหตุผลใน `spentResourceLabels`)
 *
 * **Sprint H Part 4 (frontend) — กดแล้วทอยได้เลย ไม่มีช่องกรอก** · สูตรมาจาก server (catalog มีดาเมจ/
 * ability แล้ว) และ **แสดงให้เห็นก่อนกดเสมอ** ทั้งบนการ์ดและในกล่องยืนยัน — ไม่ซ่อน เพื่อให้ผู้เล่น
 * เรียนรู้ตัวเลขไปด้วย · ท่าที่ระบบยังทอยให้ไม่ได้ (ให้เป้าเซฟ / รักษา / โดนอัตโนมัติ / server คำนวณ
 * สูตรไม่ได้) บอกให้ขอ DM แทนการเปิดช่องกรอก เพราะช่องกรอกมือมีเฉพาะหน้า DM (player-side-design §5)
 */
export function AffordancePanel({
  campaignId,
  sessionId,
  characterId,
  characterName,
  combat,
}: {
  campaignId: string;
  sessionId: string;
  characterId: string;
  characterName: string;
  combat: Combat;
}) {
  const { options, isLoading, isError } = useAffordances(campaignId, sessionId, characterId);
  const [selected, setSelected] = useState<AffordanceItem | null>(null);

  const turnState = combat.encounter?.turnState ?? null;
  const spent = spentResourceLabels(turnState);
  const groups = groupByOption(options);

  return (
    <section className={sectionClass}>
      <h2 className="font-medium">คุณจะทำอะไร?</h2>

      {isLoading && <p className="text-sm opacity-50">กำลังคำนวณตัวเลือก...</p>}
      {isError && <p className="text-sm text-red-500">โหลดตัวเลือกไม่สำเร็จ</p>}

      {!isLoading && !isError && groups.length === 0 && (
        <p className="text-sm opacity-60">
          ตอนนี้ยังไม่มีอะไรที่ {characterName} ทำได้ — ลองเข้าใกล้เป้าหมาย หรือรอให้ DM เปิดพื้นที่เพิ่ม
        </p>
      )}

      <div className="space-y-2">
        {groups.map((group) => (
          <div
            key={group.optionId}
            className="relative rounded border border-black/10 dark:border-white/10 p-3 space-y-2"
          >
            <div className="flex items-start justify-between gap-2">
              <h3 className="font-semibold uppercase text-sm tracking-wide">{group.optionName}</h3>
              <span className="text-xs opacity-60 whitespace-nowrap">{costLabel(group.cost)}</span>
            </div>

            {group.targets.map((item) => {
              const badge = advantageBadge(item.advantageMode);
              const isSelected = selected?.optionId === item.optionId && selected?.targetId === item.targetId;
              const line = targetLine(item);
              const formula = formulaLine(item);

              return (
                <div key={`${item.optionId}:${item.targetId ?? "none"}`} className="space-y-1">
                  <button
                    type="button"
                    onClick={() => setSelected(isSelected ? null : item)}
                    className={`w-full text-left rounded border px-3 py-2 text-sm transition-colors ${
                      isSelected
                        ? "border-indigo-500 ring-1 ring-indigo-500"
                        : "border-black/10 dark:border-white/10 hover:border-black/30 dark:hover:border-white/30"
                    }`}
                  >
                    <span className="flex items-center justify-between gap-2">
                      <span>{line ?? "ไม่ต้องเลือกเป้า"}</span>
                      {badge && (
                        <span
                          className={`rounded px-1.5 py-0.5 text-[10px] font-bold tracking-wide ${
                            item.advantageMode === "Disadvantage"
                              ? "bg-red-600 text-white"
                              : "bg-green-600 text-white"
                          }`}
                        >
                          {badge}
                        </span>
                      )}
                    </span>
                    {/* สูตรเห็นตั้งแต่ก่อนเลือก — mockup มีบรรทัด "สร้างดาเมจ 1d8+3" บนการ์ด */}
                    {formula && (
                      <span className="mt-0.5 block text-xs tabular-nums opacity-60">{formula}</span>
                    )}
                  </button>

                  {/* กล่องเหตุผล — หัวใจของโมเดล 3 ระดับ (§1.4): ระบบไม่ห้าม แต่บอกราคาให้ครบก่อนกด
                      ผู้เล่นเรียนกฎจากการเห็นป้ายซ้ำ ๆ โดยไม่ต้องเปิดหนังสือ */}
                  {item.notes.map((note) => (
                    <p
                      key={note.code}
                      className={`flex items-start gap-2 rounded border px-2 py-1 text-xs ${
                        item.advantageMode === "Disadvantage"
                          ? "border-red-500/40 text-red-600 dark:text-red-400"
                          : "border-green-500/40 text-green-700 dark:text-green-400"
                      }`}
                    >
                      <span aria-hidden="true">●</span>
                      <span>{note.message}</span>
                    </p>
                  ))}
                </div>
              );
            })}
          </div>
        ))}

        {/* สถานะที่ 4 ของ mockup — ทรัพยากรที่ใช้ไปแล้วในเทิร์นนี้ ท่าที่ต้องใช้มันจึงหายไปจากรายการ
            ข้างบน · บอกไว้เพื่อให้ผู้เล่นเข้าใจว่า "ทำไมตัวเลือกหายไป" ไม่ใช่คิดว่าระบบพัง */}
        {spent.map((label) => (
          <div
            key={label}
            className="rounded border border-black/10 dark:border-white/10 p-3 opacity-40"
          >
            <div className="flex items-start justify-between gap-2">
              <h3 className="font-semibold uppercase text-sm tracking-wide">{label}</h3>
              <span className="text-xs whitespace-nowrap">ใช้ไปแล้ว</span>
            </div>
            <p className="text-xs">ท่าที่ต้องใช้ {label} จะกลับมาในเทิร์นหน้า</p>
          </div>
        ))}
      </div>

      {selected && (
        <ActionConfirm
          key={`${selected.optionId}:${selected.targetId ?? "none"}`}
          combat={combat}
          attackerId={characterId}
          item={selected}
        />
      )}
    </section>
  );
}

/**
 * กล่องยืนยันหลังเลือกการ์ด — **ไม่มีช่องกรอก** มีแต่สูตรที่ระบบจะใช้พร้อมคำอธิบายภาษาคน แล้วปุ่มเดียว
 *
 * novice check ของ Part นี้: ผู้เล่นที่ไม่รู้ว่า `1d8+3` แปลว่าอะไร ต้องโจมตีได้จบโดยไม่พิมพ์อะไรเลย ·
 * ส่วนคำอธิบายใต้สูตรมีไว้ให้คนที่อยากรู้ค่อย ๆ เรียน ไม่ได้บังคับให้อ่านก่อนกด
 */
function ActionConfirm({
  combat,
  attackerId,
  item,
}: {
  combat: Combat;
  attackerId: string;
  item: AffordanceItem;
}) {
  const plan = attackPlan(item);
  const formula = formulaLine(item);

  return (
    <div className="rounded border border-indigo-500 p-3 space-y-2">
      <p className="text-sm">
        {item.optionName}
        {item.targetName && (
          <>
            {" "}→ <strong>{item.targetName}</strong>
          </>
        )}
      </p>

      {plan.kind === "attack" ? (
        <>
          <dl className="space-y-1.5 text-sm">
            <div>
              <dt className="inline">ทอยโจมตี </dt>
              <dd className="inline font-medium tabular-nums">{plan.expression}</dd>
              {/* SRD 5.2: "...equals or exceeds the target's Armor Class" = โดน */}
              <p className="text-xs opacity-60">
                {describeDice(plan.expression) ?? plan.expression} — ได้เท่ากับหรือมากกว่า AC ของเป้าคือโดน
              </p>
            </div>
            <div>
              <dt className="inline">ดาเมจ </dt>
              <dd className="inline font-medium tabular-nums">
                {plan.damageExpression} {damageTypeLabel(plan.damageType)}
              </dd>
              <p className="text-xs opacity-60">
                {describeDice(plan.damageExpression) ?? plan.damageExpression} — ทอยเฉพาะตอนโจมตีโดน
              </p>
            </div>
          </dl>

          {/* ใช้ถ้อยคำเดียวกับป้ายเหตุผลที่ server เขียนมา ("ทอย 2 ลูก เอาลูกที่แย่กว่า") ให้สอดคล้องกัน */}
          {plan.advantage === "Disadvantage" && (
            <p className="text-xs text-red-600 dark:text-red-400">
              เสียเปรียบ — ระบบทอย 2 ลูก เอาลูกที่แย่กว่าให้เอง
            </p>
          )}
          {plan.advantage === "Advantage" && (
            <p className="text-xs text-green-700 dark:text-green-400">
              ได้เปรียบ — ระบบทอย 2 ลูก เอาลูกที่ดีกว่าให้เอง
            </p>
          )}

          <button
            type="button"
            onClick={() =>
              combat.makeAttack.mutate({
                attackerId,
                targetCharacterId: plan.targetId,
                expression: plan.expression,
                damageExpression: plan.damageExpression,
                damageType: plan.damageType,
                advantage: plan.advantage,
              })
            }
            disabled={combat.makeAttack.isPending}
            className="rounded bg-indigo-600 px-4 py-1.5 text-white disabled:opacity-50"
          >
            {combat.makeAttack.isPending ? "กำลังทอย..." : "ลงมือ"}
          </button>
        </>
      ) : (
        <>
          {formula && <p className="text-sm tabular-nums">{formula}</p>}
          <p className="text-sm opacity-70">{plan.reason}</p>
        </>
      )}
    </div>
  );
}

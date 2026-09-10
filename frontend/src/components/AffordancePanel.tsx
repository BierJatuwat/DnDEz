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
import type { AffordanceItem } from "@/lib/affordance-types";
import type { useCombatActions } from "@/lib/use-combat-actions";

/** แพทเทิร์นเดียวกับ combat-panels.tsx — ผูกกับ return type ของ hook ตรง ๆ ไม่ประกาศ interface ซ้ำ */
type Combat = ReturnType<typeof useCombatActions>;

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";
const inputClass =
  "rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5";

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
 * ⚠️ **ยังต้องกรอกสูตรเต๋าอยู่ และนี่เป็นข้อจำกัดที่รู้ตัว ไม่ใช่ลืมทำ** — mockup แสดงบรรทัด
 * "สร้างดาเมจ 1d8+3" บนการ์ด แต่ **คลังกลไกไม่มีข้อมูลดาเมจเลย** (design doc §2.3 ระบุสิ่งที่
 * catalog เก็บไว้ชัดเจน: ระยะ/ชนิดเป้า/ราคา/รูปทอย/ป้ายประเภท — ไม่มีดาเมจ) และค่าโจมตีก็ต้องรู้ว่า
 * ท่าไหนใช้ STR หรือ DEX ซึ่ง catalog ก็ไม่ได้บอก · จึงเลือก **ไม่แต่งตัวเลขขึ้นมาเอง** — สิ่งที่
 * Part นี้แก้ได้จริงคือ "เลือกอะไรกับใคร" ซึ่งย้ายมาให้ server คำนวณครบแล้ว ส่วนตัวเลขเหลือเป็นช่อง
 * รองที่พับเก็บไว้ รอ Part ที่เพิ่มดาเมจ/ability ลง catalog
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

      {selected && selected.targetId && (
        <RollControls
          key={`${selected.optionId}:${selected.targetId}`}
          combat={combat}
          attackerId={characterId}
          target={selected}
        />
      )}
    </section>
  );
}

/**
 * ช่องตัวเลขที่เหลืออยู่ — โผล่เฉพาะหลังเลือกการ์ดแล้ว เพื่อให้ "เลือกอะไรกับใคร" (ที่คำนวณมาแล้ว)
 * เป็นพระเอกของพาเนล ส่วนตัวเลขเป็นของรอง · ดู doc comment ของ AffordancePanel ว่าทำไมยังคำนวณ
 * สูตรให้อัตโนมัติไม่ได้ในรอบนี้
 */
function RollControls({
  combat,
  attackerId,
  target,
}: {
  combat: Combat;
  attackerId: string;
  target: AffordanceItem;
}) {
  const [expression, setExpression] = useState("1d20+5");
  const [damageExpression, setDamageExpression] = useState("1d8+3");

  return (
    <div className="rounded border border-indigo-500 p-3 space-y-2">
      <p className="text-sm">
        {target.optionName} → <strong>{target.targetName}</strong>
        {target.advantageMode === "Disadvantage" && (
          <span className="ml-2 text-xs text-red-600 dark:text-red-400">(ทอยแบบเสียเปรียบ)</span>
        )}
      </p>
      <div className="flex flex-wrap gap-2">
        <input
          value={expression}
          onChange={(e) => setExpression(e.target.value)}
          className={`w-28 text-sm ${inputClass}`}
          aria-label="สูตรทอยโจมตี"
        />
        <input
          value={damageExpression}
          onChange={(e) => setDamageExpression(e.target.value)}
          className={`w-28 text-sm ${inputClass}`}
          aria-label="สูตรดาเมจ"
        />
        <button
          type="button"
          onClick={() =>
            combat.makeAttack.mutate({
              attackerId,
              targetCharacterId: target.targetId!,
              expression,
              damageExpression,
              damageType: "Slashing",
            })
          }
          disabled={combat.makeAttack.isPending}
          className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
        >
          ลงมือ
        </button>
      </div>
    </div>
  );
}

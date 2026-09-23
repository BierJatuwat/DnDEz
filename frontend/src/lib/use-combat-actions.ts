"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch, ApiError } from "./api";
import { useAuthStore } from "./auth-store";
import type { AdvantageMode } from "./affordance-types";
import type {
  AttackResult,
  CheckResult,
  DeathSaveState,
  Encounter,
  LatestCombatEvent,
  NextTurnResult,
  SpendTurnResourceResult,
} from "./combat-types";

/**
 * Sprint F งานที่ 6b — encounter / turn order / death save / attack / check
 *
 * backend ทำไว้ครบตั้งแต่งานที่ 4-5 (combat ตั้งแต่ Sprint B Part 3) แต่ไม่เคยมี UI มาก่อนเลย
 *
 * แยกจาก `use-campaign-workspace.ts` โดยตั้งใจ: workspace เป็นของที่ *ทุกหน้า* ต้องใช้เสมอ
 * (เซสชัน/ตัวละคร/แชท/hub) ส่วนก้อนนี้ใช้เฉพาะตอนอยู่ในเซสชันที่กำลังสู้กันจริง และมี query key
 * ชุดของตัวเอง — รวมไว้ไฟล์เดียวกันจะทำให้ไฟล์ที่ใหญ่อยู่แล้วอ่านยากขึ้นโดยไม่ได้อะไรกลับมา
 */
export function useCombatActions({
  campaignId,
  sessionId,
  appendLog,
}: {
  campaignId: string;
  sessionId: string | null;
  appendLog: (text: string) => void;
}) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  /**
   * ผลของ action ล่าสุดที่ผู้ใช้คนนี้สั่ง — เก็บเป็น state ของ hook แทนการอ่าน `mutation.data`
   * ของแต่ละตัวแล้วมาเทียบว่าอันไหนใหม่กว่า เพราะ "ลำดับ" คือสิ่งที่แถบนี้ต้องการ และการ set
   * ตรงจุด onSuccess ให้ลำดับที่ถูกต้องโดยนิยาม ไม่ต้องพึ่ง timestamp ของ library
   */
  const [latestEvent, setLatestEvent] = useState<LatestCombatEvent | null>(null);

  const encounterQuery = useQuery({
    queryKey: ["encounter-active", sessionId],
    // 204 No Content (ไม่มี encounter เปิดอยู่) → apiFetch คืน null ไม่ throw
    queryFn: () =>
      apiFetch<Encounter | null>(
        `/campaigns/${campaignId}/sessions/${sessionId}/encounters/active`,
        { token },
      ),
    enabled: !!token && !!campaignId && !!sessionId,
  });

  const invalidateEncounter = () =>
    queryClient.invalidateQueries({ queryKey: ["encounter-active"] });

  const startEncounter = useMutation({
    mutationFn: (characterIds: string[]) =>
      apiFetch<Encounter>(`/campaigns/${campaignId}/sessions/${sessionId}/encounters`, {
        method: "POST",
        token,
        body: { characterIds },
      }),
    onSuccess: async () => {
      await invalidateEncounter();
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        // 409 = ยังมี encounter ที่ไม่จบค้างอยู่ในเซสชันนี้ (ต้องจบอันเดิมก่อน)
        if (err.status === 409) appendLog("เปิด encounter ไม่สำเร็จ: ยังมี encounter ที่ยังไม่จบอยู่");
        else if (err.status === 400) appendLog("เปิด encounter ไม่สำเร็จ: เลือกตัวละครไม่ถูกต้อง");
        else appendLog(`เปิด encounter ไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  const nextTurn = useMutation({
    mutationFn: (encounterId: string) =>
      apiFetch<NextTurnResult>(`/campaigns/${campaignId}/encounters/${encounterId}/next-turn`, {
        method: "POST",
        token,
      }),
    onSuccess: async () => {
      await invalidateEncounter();
    },
    onError: (err) => {
      if (err instanceof ApiError) appendLog(`ขึ้นเทิร์นถัดไปไม่สำเร็จ: HTTP ${err.status}`);
    },
  });

  const endEncounter = useMutation({
    mutationFn: (encounterId: string) =>
      apiFetch<{ encounterId: string }>(`/campaigns/${campaignId}/encounters/${encounterId}/end`, {
        method: "POST",
        token,
      }),
    onSuccess: async () => {
      await invalidateEncounter();
      // มอนสเตอร์ที่ spawn ถูกลบ (ตัว + token) และมีสรุปผลลงบันทึก — ไม่รอ broadcast `EncounterEnded`
      // เผื่อ realtime หลุดอยู่ คนกดจบเองต้องเห็นผลทันที
      await queryClient.invalidateQueries({ queryKey: ["characters"] });
      await queryClient.invalidateQueries({ queryKey: ["scene"] });
      await queryClient.invalidateQueries({ queryKey: ["journal"] });
    },
    onError: (err) => {
      if (err instanceof ApiError) appendLog(`จบ encounter ไม่สำเร็จ: HTTP ${err.status}`);
    },
  });

  const spendTurnResource = useMutation({
    mutationFn: ({
      encounterId,
      resource,
      feet,
    }: {
      encounterId: string;
      resource: string;
      feet?: number;
    }) =>
      apiFetch<SpendTurnResourceResult>(
        `/campaigns/${campaignId}/encounters/${encounterId}/turn-actions/spend`,
        { method: "POST", token, body: { resource, feet } },
      ),
    onSuccess: async () => {
      await invalidateEncounter();
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        // 400 = ใช้ซ้ำในเทิร์นเดียว/movement เกินที่เหลือ · 403 = ไม่ใช่เทิร์นของตัวละครที่คุมอยู่
        // 409 = CAS ชนกัน (มีคนแก้พร้อมกัน) · 503 = Redis เขียนไม่ได้ชั่วคราว
        if (err.status === 400) appendLog("ใช้ทรัพยากรไม่สำเร็จ: ใช้ไปแล้วในเทิร์นนี้ หรือเกินที่เหลือ");
        else if (err.status === 403) appendLog("ใช้ทรัพยากรไม่สำเร็จ: ไม่ใช่เทิร์นของตัวละครที่คุณคุม");
        else if (err.status === 409) appendLog("ใช้ทรัพยากรไม่สำเร็จ: มีการแก้พร้อมกัน ลองใหม่อีกครั้ง");
        else if (err.status === 503) appendLog("ใช้ทรัพยากรไม่สำเร็จ: turn state ใช้งานไม่ได้ชั่วคราว");
        else appendLog(`ใช้ทรัพยากรไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  /**
   * สถานะ death save ของตัวละครหนึ่งตัว — API คืน `status: "NotDying"` เมื่อยังไม่มีแถวในตาราง
   * (ไม่ใช่ 404) ฝั่งนี้จึงไม่ต้องแยกเคส "ไม่มีข้อมูล" กับ "ไม่ได้กำลังจะตาย"
   *
   * ⚠️ เมื่อแคมเปญตั้ง `DeathSaveVisibility = DmOnly` ผู้เล่นจะได้ 403 จาก endpoint นี้ — ไม่ใช่บั๊ก
   * แต่เป็นค่าที่ DM ตั้งไว้เอง (ดู DeathSaveEndpoints.cs) UI จึงต้องไม่โชว์ error ให้ตกใจ
   */
  const useDeathSave = (characterId: string | null) =>
    useQuery({
      queryKey: ["death-saves", characterId],
      queryFn: () =>
        apiFetch<DeathSaveState>(
          `/campaigns/${campaignId}/characters/${characterId}/death-saves`,
          { token },
        ),
      enabled: !!token && !!campaignId && !!characterId,
      retry: false,
    });

  const confirmDying = useMutation({
    mutationFn: (characterId: string) =>
      apiFetch<DeathSaveState>(
        `/campaigns/${campaignId}/characters/${characterId}/death-saves/confirm-dying`,
        { method: "POST", token },
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["death-saves"] });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        // 400 = HP ยังไม่ถึง 0 · 409 = ยืนยันไปแล้ว หรือจบไปแล้ว (Stable/Dead)
        if (err.status === 400) appendLog("ยืนยัน dying ไม่สำเร็จ: ตัวละครยังไม่ได้อยู่ที่ 0 HP");
        else if (err.status === 409) appendLog("ยืนยัน dying ไม่สำเร็จ: อยู่ในสถานะนั้นอยู่แล้ว");
        else appendLog(`ยืนยัน dying ไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  const rollDeathSave = useMutation({
    mutationFn: (characterId: string) =>
      apiFetch<{ outcome: string; successes: number; failures: number; status: string }>(
        // sessionId เป็น route segment ของ endpoint นี้ (ไม่ใช่ body) เพราะการทอยต้องบันทึกลง
        // dice_rolls ซึ่งผูกกับเซสชัน — ต่างจาก confirm-dying/GET ที่ไม่มี segment นี้
        `/campaigns/${campaignId}/sessions/${sessionId}/characters/${characterId}/death-saves/roll`,
        { method: "POST", token },
      ),
    onSuccess: async (r, characterId) => {
      setLatestEvent({
        kind: "death-save",
        characterId,
        outcome: r.outcome,
        successes: r.successes,
        failures: r.failures,
      });
      appendLog(
        `💀 death save: ${r.outcome} (สำเร็จ ${r.successes}/3 · ล้มเหลว ${r.failures}/3 · ${r.status})`,
      );
      await queryClient.invalidateQueries({ queryKey: ["death-saves"] });
      await queryClient.invalidateQueries({ queryKey: ["characters"] });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        if (err.status === 409) appendLog("ทอย death save ไม่สำเร็จ: ตัวละครไม่ได้อยู่ในสถานะ Rolling");
        else appendLog(`ทอย death save ไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  const makeAttack = useMutation({
    mutationFn: ({
      attackerId,
      targetCharacterId,
      expression,
      damageExpression,
      damageType,
      advantage,
    }: {
      attackerId: string;
      targetCharacterId: string;
      expression: string;
      damageExpression: string;
      damageType: string;
      /** Sprint H Part 4 — โหมดที่ affordance คำนวณมา (ก่อนหน้านี้ไม่เคยส่ง การ์ดขึ้น "เสียเปรียบ"
       *  แต่ทอยปกติเงียบ ๆ) · ไม่ส่ง = server ใช้ None ตามเดิม ฟอร์มกรอกมือของ DM จึงไม่กระทบ */
      advantage?: AdvantageMode;
    }) =>
      apiFetch<AttackResult>(
        `/campaigns/${campaignId}/sessions/${sessionId}/characters/${attackerId}/attacks`,
        {
          method: "POST",
          token,
          body: {
            expression,
            advantage,
            targetCharacterId,
            damageComponents: [{ expression: damageExpression, type: damageType }],
          },
        },
      ),
    onSuccess: (r, variables) => {
      setLatestEvent({
        kind: "attack",
        attackerId: variables.attackerId,
        targetId: variables.targetCharacterId,
        total: r.total,
        hit: r.hit,
        damage: r.totalDamage,
      });
      // targetArmorClass เป็น null ได้เมื่อเป้าหมายเป็น NPC ที่ DM ซ่อน stat ไว้ (งานที่ 7)
      const vsAc = r.targetArmorClass !== null ? ` vs AC ${r.targetArmorClass}` : "";
      appendLog(
        r.hit
          ? `⚔️ ${r.outcome}: ${r.total}${vsAc} → ดาเมจ ${r.totalDamage ?? 0}`
          : `⚔️ ${r.outcome}: ${r.total}${vsAc} (พลาด)`,
      );
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        if (err.status === 403) appendLog("โจมตีไม่สำเร็จ: ไม่ใช่ตัวละครที่คุณคุม");
        else if (err.status === 400) appendLog("โจมตีไม่สำเร็จ: สูตรทอยหรือชนิดดาเมจไม่ถูกต้อง");
        else appendLog(`โจมตีไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  const makeCheck = useMutation({
    mutationFn: ({
      characterId,
      expression,
      dc,
    }: {
      characterId: string;
      expression: string;
      dc: number;
    }) =>
      apiFetch<CheckResult>(
        `/campaigns/${campaignId}/sessions/${sessionId}/characters/${characterId}/checks`,
        { method: "POST", token, body: { expression, dc } },
      ),
    onSuccess: (r, variables) => {
      setLatestEvent({
        kind: "check",
        characterId: variables.characterId,
        total: r.total,
        dc: r.dc,
        success: r.success,
      });
      appendLog(
        `🎯 เช็ค ${r.expression} = ${r.total} vs DC ${r.dc} → ${r.success ? "ผ่าน" : "ไม่ผ่าน"}`,
      );
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        if (err.status === 403) appendLog("ทอยเช็คไม่สำเร็จ: ไม่ใช่ตัวละครที่คุณคุม");
        else if (err.status === 400) appendLog("ทอยเช็คไม่สำเร็จ: สูตรทอยหรือ DC ไม่ถูกต้อง");
        else appendLog(`ทอยเช็คไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  return {
    encounter: encounterQuery.data ?? null,
    encounterQuery,
    latestEvent,
    startEncounter,
    nextTurn,
    endEncounter,
    spendTurnResource,
    useDeathSave,
    confirmDying,
    rollDeathSave,
    makeAttack,
    makeCheck,
  };
}

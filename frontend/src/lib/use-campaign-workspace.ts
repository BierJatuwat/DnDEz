"use client";

import { useEffect } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuthStore } from "./auth-store";
import { useConsoleStore } from "./console-store";
import { apiFetch, ApiError } from "./api";
import { useCampaignHub } from "./use-campaign-hub";
import {
  normalizeProposalCreated,
  mergeProposal,
  removeProposal,
  type Proposal,
} from "./proposal-queue";
import { normalizeReactionPrompt } from "./reaction-prompt";
import { normalizeActionMessageCreated } from "./action-feed";
import type {
  AutonomySetting,
  CharacterSummary,
  ConsultResponse,
  DiceRolledEvent,
  GameSession,
  HpUpdatedEvent,
  ResolveProposalResponse,
} from "./campaign-types";

/**
 * Sprint F งานที่ 6a — plumbing ทั้งหมดที่ **ทั้งหน้า DM และหน้าผู้เล่นใช้ร่วมกัน** (query, mutation,
 * การต่อ SignalR, binding เข้า console-store) แยกออกมาจาก `CampaignConsole.tsx` เดิมที่รวมทุกอย่าง
 * ไว้ในคอมโพเนนต์เดียว
 *
 * ทำไมเป็น hook ไม่ใช่คอมโพเนนต์กลางที่รับ `role` แล้ว render แยกเอง: สองหน้ามี layout/ลำดับ section
 * ต่างกันจริง (ไม่ใช่แค่ซ่อนบางปุ่ม) การรวมไว้ในคอมโพเนนต์เดียวแล้วแตกด้วย `role ? A : B` ทำให้ไฟล์
 * เดิมที่ยาวอยู่แล้วยาวขึ้นอีกและอ่านไม่ออกว่าฝั่งไหนเห็นอะไรบ้าง — แยก "ข้อมูล" (hook นี้) ออกจาก
 * "การนำเสนอ" (DmConsole/PlayerView) แทน
 *
 * ⚠️ `campaignId` มาจาก **route param** ไม่ใช่จาก store อีกต่อไป (เดิมเป็น state ในหน้าเดียว) —
 * hook นี้ sync ค่าลง store ให้เองเพื่อให้ตรรกะ cascade-clear เดิมของ store (เปลี่ยนแคมเปญ =
 * ล้าง transcript/action feed) ยังทำงานเหมือนเดิม
 */
export function useCampaignWorkspace(campaignId: string) {
  const token = useAuthStore((s) => s.token);
  const userId = useAuthStore((s) => s.userId);
  const queryClient = useQueryClient();

  const selectedCampaignId = useConsoleStore((s) => s.selectedCampaignId);
  const selectedSessionId = useConsoleStore((s) => s.selectedSessionId);
  const joinedCampaignId = useConsoleStore((s) => s.joinedCampaignId);
  const hubStatus = useConsoleStore((s) => s.hubStatus);
  const log = useConsoleStore((s) => s.log);
  const rollExpression = useConsoleStore((s) => s.rollExpression);
  const actionInput = useConsoleStore((s) => s.actionInput);
  const actionFeed = useConsoleStore((s) => s.actionFeed);
  const reactionPrompt = useConsoleStore((s) => s.reactionPrompt);
  const setReactionPrompt = useConsoleStore((s) => s.setReactionPrompt);
  const setSelectedCampaignId = useConsoleStore((s) => s.setSelectedCampaignId);
  const setSelectedSessionId = useConsoleStore((s) => s.setSelectedSessionId);
  const setJoinedCampaignId = useConsoleStore((s) => s.setJoinedCampaignId);
  const setHubStatus = useConsoleStore((s) => s.setHubStatus);
  const setRollExpression = useConsoleStore((s) => s.setRollExpression);
  const appendLog = useConsoleStore((s) => s.appendLog);
  const setActionInput = useConsoleStore((s) => s.setActionInput);
  const appendActionFeedMessage = useConsoleStore((s) => s.appendActionFeedMessage);

  // route คือแหล่งความจริงของ "กำลังดูแคมเปญไหนอยู่" — store ตามหลัง (ไม่ใช่ทางกลับกันเหมือนเดิม)
  // setSelectedCampaignId มี cascade-clear ในตัว (ล้าง session/transcript/action feed) จึงต้องเรียก
  // เฉพาะตอนค่าต่างจริง ไม่งั้นจะล้าง transcript ทิ้งทุก re-render
  useEffect(() => {
    if (selectedCampaignId !== campaignId) {
      setSelectedCampaignId(campaignId);
    }
  }, [campaignId, selectedCampaignId, setSelectedCampaignId]);

  const sessionsQuery = useQuery({
    queryKey: ["sessions", campaignId],
    queryFn: () => apiFetch<GameSession[]>(`/campaigns/${campaignId}/sessions`, { token }),
    enabled: !!token && !!campaignId,
  });

  const charactersQuery = useQuery({
    queryKey: ["characters", campaignId],
    queryFn: () =>
      apiFetch<CharacterSummary[]>(`/campaigns/${campaignId}/characters`, { token }),
    enabled: !!token && !!campaignId,
  });

  const proposalsQuery = useQuery({
    queryKey: ["proposals", selectedSessionId],
    queryFn: () =>
      apiFetch<Proposal[]>(
        `/campaigns/${campaignId}/sessions/${selectedSessionId}/proposals`,
        { token },
      ),
    enabled: !!token && !!campaignId && !!selectedSessionId,
  });

  // GET เปิดให้สมาชิกทุกคนอ่านได้ (ไม่ใช่ DM-only) — ดู AutonomySettingEndpoints.cs
  const autonomySettingsQuery = useQuery({
    queryKey: ["autonomy-settings", campaignId],
    queryFn: () =>
      apiFetch<AutonomySetting[]>(`/campaigns/${campaignId}/autonomy-settings`, { token }),
    enabled: !!token && !!campaignId,
  });

  const createSession = useMutation({
    mutationFn: () =>
      apiFetch<{ id: string }>(`/campaigns/${campaignId}/sessions`, {
        method: "POST",
        token,
        body: { mode: "Online" },
      }),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: ["sessions", campaignId] });
      setSelectedSessionId(created.id);
    },
  });

  const createCharacter = useMutation({
    // MVP: ฟอร์มสร้างตัวละครรับแค่ชื่อ ฟิลด์อื่น (HP/AC/ระดับ ฯลฯ) ปล่อยให้ฝั่ง server ใส่ default
    // ให้ ยังไม่ทำ character-sheet editor เต็มรูปแบบในสปรินต์นี้
    mutationFn: (name: string) =>
      apiFetch<{ id: string }>(`/campaigns/${campaignId}/characters`, {
        method: "POST",
        token,
        body: { name },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["characters", campaignId] });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        appendLog(`สร้างตัวละครไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  const rollDice = useMutation({
    mutationFn: () =>
      apiFetch<{ total: number; expression: string }>(
        `/campaigns/${campaignId}/sessions/${selectedSessionId}/dice-rolls`,
        { method: "POST", token, body: { expression: rollExpression } },
      ),
    onSuccess: (result) => {
      appendLog(
        `✅ REST response: ${result.expression} = ${result.total} (รอ broadcast ตามมา...)`,
      );
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        appendLog(`ทอยลูกเต๋าไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  // DM-only ที่ชั้น API (403 ถ้าไม่ใช่ DM) — ปุ่มถูกซ่อนสำหรับ non-DM อยู่แล้ว แต่ error handling
  // ไว้เผื่อ race (เช่นถูกถอดจาก DM ระหว่างที่ฟอร์มเปิดค้างอยู่)
  const setAutonomyLevel = useMutation({
    mutationFn: ({ subsystem, level }: { subsystem: string; level: string }) =>
      apiFetch<AutonomySetting>(
        `/campaigns/${campaignId}/autonomy-settings/${subsystem}`,
        { method: "PUT", token, body: { level } },
      ),
    onSuccess: (result) => {
      queryClient.setQueryData<AutonomySetting[]>(
        ["autonomy-settings", campaignId],
        (old) =>
          old?.map((s) => (s.subsystem === result.subsystem ? result : s)) ?? [result],
      );
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        appendLog(`ตั้งค่า autonomy ไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  // Sprint E Part 3 — player action input: ปุ่ม/free text ทั้งสองทางส่ง content เดียวกันมาที่นี่
  // (ดู player-side-design.md §2 — ปุ่มเป็นแค่ template ข้อความ ไม่ใช่เส้นทางแยก) ไหลเข้า
  // orchestrator เดียวกับ /consult ฝั่ง API (OrchestratorTurnRunner) แต่ auth เป็นเจ้าของตัวละคร
  const postAction = useMutation({
    mutationFn: ({ characterId, content }: { characterId: string; content: string }) =>
      apiFetch<ConsultResponse>(
        `/campaigns/${campaignId}/sessions/${selectedSessionId}/actions`,
        { method: "POST", token, body: { characterId, content } },
      ),
    onError: (err) => {
      if (err instanceof ApiError) {
        if (err.status === 403) {
          appendLog("ส่ง action ไม่สำเร็จ: ไม่ใช่เจ้าของตัวละครนี้");
        } else if (err.status === 400) {
          appendLog("ส่ง action ไม่สำเร็จ: DM ยังไม่ได้ตั้งค่า BYOK key");
        } else {
          appendLog(`ส่ง action ไม่สำเร็จ: HTTP ${err.status}`);
        }
      }
    },
  });

  const resolveProposal = useMutation({
    mutationFn: ({
      proposalId,
      action,
    }: {
      proposalId: string;
      action: "approve" | "reject";
    }) =>
      apiFetch<ResolveProposalResponse>(
        `/campaigns/${campaignId}/proposals/${proposalId}/resolve`,
        { method: "POST", token, body: { action } },
      ),
    onSuccess: (result, variables) => {
      // list เป็น pending-only เมื่อ resolve สำเร็จคือไม่ pending อีกต่อไปแน่นอน ตัดออกจาก cache ตรง ๆ
      // ด้วย setQueryData ดีกว่า invalidateQueries เพราะไม่ต้อง round-trip fetch ใหม่ และให้พฤติกรรม
      // สอดคล้องกับฝั่ง realtime merge (onProposalCreated) ที่ใช้ setQueryData เหมือนกัน
      queryClient.setQueryData<Proposal[]>(["proposals", selectedSessionId], (old) =>
        removeProposal(old, variables.proposalId),
      );
      appendLog(`${variables.action === "approve" ? "อนุมัติ" : "ปฏิเสธ"} proposal สำเร็จ`);
      // ผลของ AI หลัง approve เคยแสดงใน consultMessages (ช่องปรึกษา AI เดิมที่ถูกลบไปแล้ว) — ตอนนี้ไม่มี
      // ที่แสดงบทสนทนาแบบนั้นในหน้า DM แล้ว log ไว้ให้เห็นอย่างน้อยว่า AI ตอบว่าอะไร
      if (variables.action === "approve" && result.content) {
        appendLog(`AI: ${result.content}`);
      }
    },
    onError: (err, variables) => {
      if (err instanceof ApiError && err.status === 409) {
        // 409 = มีคนอื่น resolve ไปก่อนแล้ว (race) — ก็ไม่ pending อีกต่อไปเหมือนกัน ตัดออกจาก cache
        queryClient.setQueryData<Proposal[]>(["proposals", selectedSessionId], (old) =>
          removeProposal(old, variables.proposalId),
        );
        appendLog("proposal นี้ถูกจัดการไปแล้วโดยคนอื่น (ตัดออกจากคิวแล้ว)");
      } else if (err instanceof ApiError) {
        appendLog(`จัดการ proposal ไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  // SignalR connection lifecycle + group rejoin-on-reconnect: ตรรกะจริงอยู่ใน campaign-hub-manager.ts
  useCampaignHub({
    token,
    selectedCampaignId: campaignId,
    onStatusChange: setHubStatus,
    onJoinedChange: setJoinedCampaignId,
    onLog: appendLog,
    onDiceRolled: (payload) => {
      const event = payload as DiceRolledEvent;
      appendLog(
        `📡 realtime DiceRolled: ${event.expression} = ${event.total} (source=${event.source})`,
      );
    },
    onHpUpdated: (payload) => {
      // payload ไม่มี campaignId มาด้วย และ callback นี้ถูกสมัครครั้งเดียวต่อ token (ห้ามอ่าน
      // campaignId จาก closure ตรง ๆ เพราะจะ stale) จึงใช้ setQueriesData แพตช์ทุก cache ที่ขึ้นต้น
      // ด้วย "characters" — ปลอดภัยเพราะแพตช์เฉพาะตัวละครที่ id ตรงกัน
      const event = payload as HpUpdatedEvent;
      if (typeof event?.characterId !== "string") return;
      queryClient.setQueriesData<CharacterSummary[]>({ queryKey: ["characters"] }, (old) =>
        old?.map((c) =>
          c.id === event.characterId
            ? { ...c, currentHp: event.newHp, currentTempHp: event.newTempHp }
            : c,
        ),
      );
      appendLog(
        `📡 realtime HpUpdated: ${event.previousHp} → ${event.newHp} HP` +
          (event.isInstantDeath ? " (instant death!)" : event.droppedToZero ? " (0 HP)" : ""),
      );
    },
    onCombatEvent: (name) => {
      appendLog("realtime " + name);
      void queryClient.invalidateQueries({ queryKey: ["encounter-active"] });
      if (name === "DeathSaveStateChanged" || name === "DeathSaveRolled") {
        void queryClient.invalidateQueries({ queryKey: ["death-saves"] });
        void queryClient.invalidateQueries({ queryKey: ["characters"] });
      }
      if (name === "EncounterEnded") {
        // server ลบมอนสเตอร์ที่ spawn มา (แถว character + token บนแผนที่) และเขียนสรุปผลลงบันทึกแคมเปญ
        // ในธุรกรรมเดียวกับการจบ encounter — ต้องดึงทั้งสามอย่างใหม่ ไม่งั้นจอค้างตัวที่ไม่มีอยู่แล้ว
        void queryClient.invalidateQueries({ queryKey: ["characters"] });
        void queryClient.invalidateQueries({ queryKey: ["scene"] });
        void queryClient.invalidateQueries({ queryKey: ["journal"] });
      }
    },
    onFogChanged: () => {
      // Sprint G Part 6 — DM เปิด/ปิดพื้นที่ แปลว่า "รายการ token ที่เรามีสิทธิ์เห็นเปลี่ยนไปแล้ว"
      // ต้องดึงใหม่จาก server เท่านั้น ห้ามคำนวณเองจาก payload (payload บอกแค่ขอบเขตหมอก ไม่ได้บอกว่า
      // มีใครอยู่ในนั้น — การเดาเองฝั่ง client คือการสร้างข้อมูลที่ server ตั้งใจไม่ส่งมา)
      appendLog("realtime FogChanged — ดึงแผนที่ใหม่");
      void queryClient.invalidateQueries({ queryKey: ["scene"] });
    },
    onTerrainChanged: () => {
      // Sprint H Part 6 — DM วาดหนองน้ำ/กำแพง: สิ่งที่เห็นบนแผนที่เปลี่ยน และ "เดินได้ถึงไหน" ก็เปลี่ยน
      // ต้องดึงทั้งสองอย่างใหม่ ไม่งั้นขอบเขตสีเขียวตอนลากจะยังเป็นของก่อนมีหนองน้ำ · payload ไม่มีรายการ
      // ช่องโดยตั้งใจ ห้ามพยายามวาดจาก payload (พื้นในหมอกเป็นความลับ ต้องผ่านการกรองของ GET scene)
      appendLog("realtime TerrainChanged — ดึงแผนที่และระยะเดินใหม่");
      void queryClient.invalidateQueries({ queryKey: ["scene"] });
      void queryClient.invalidateQueries({ queryKey: ["reachable"] });
    },
    onReactionPrompt: (payload) => {
      // Sprint G Part 10 — event นี้ยิงมาที่กลุ่มของผู้ใช้คนเดียว ถ้ามาถึงเราแปลว่าเรามีสิทธิ์จริง
      // (ไม่ต้องกรองซ้ำที่นี่) · เก็บไว้ใน state ให้ component เด้ง prompt ขึ้นมา
      const prompt = normalizeReactionPrompt(payload);
      if (!prompt) {
        appendLog("ได้รับ ReactionPrompt ที่ข้อมูลไม่ครบ ข้ามไป");
        return;
      }
      appendLog(`realtime ReactionPrompt — ${prompt.threatName} มีสิทธิ์สวน`);
      setReactionPrompt(prompt);
    },
    onMovementResumed: () => {
      // การเดินที่ค้างอยู่ไปต่อแล้ว (มีคนตอบ หรือ sweeper ปิดให้เพราะหมดเวลา) → ตำแหน่ง token เปลี่ยน
      appendLog("realtime MovementResumed — ดึงแผนที่ใหม่");
      void queryClient.invalidateQueries({ queryKey: ["scene"] });
    },
    onActionMessageCreated: (payload) => {
      const message = normalizeActionMessageCreated(payload);
      if (!message) {
        appendLog("ได้รับ ActionMessageCreated broadcast ที่ข้อมูลไม่ครบ ข้ามไป");
        return;
      }
      appendActionFeedMessage(message);
    },
    onProposalCreated: (payload) => {
      const proposal = normalizeProposalCreated(payload);
      if (!proposal) {
        appendLog("ได้รับ ProposalCreated broadcast ที่ข้อมูลไม่ครบ ข้ามไป");
        return;
      }
      // ใช้ proposal.sessionId ของ payload เองเป็น key ไม่อ่าน state สด (กัน stale closure) —
      // broadcast กระจายทั้งกลุ่มแคมเปญ ต้อง merge เข้า cache ของเซสชันเจ้าของ proposal นั้นจริง ๆ
      queryClient.setQueryData<Proposal[]>(["proposals", proposal.sessionId], (old) =>
        mergeProposal(old, proposal),
      );
      appendLog(
        `📡 realtime ProposalCreated: ${proposal.toolName} (session ${proposal.sessionId.slice(0, 8)})`,
      );
    },
  });

  const characters = charactersQuery.data ?? [];

  return {
    userId,
    reactionPrompt,
    clearReactionPrompt: () => setReactionPrompt(null),
    // server state
    sessions: sessionsQuery.data ?? [],
    characters,
    ownCharacters: characters.filter((c) => c.ownerUserId === userId),
    proposals: proposalsQuery.data ?? [],
    autonomySettings: autonomySettingsQuery.data ?? [],
    proposalsQuery,
    autonomySettingsQuery,
    // ui state (console-store)
    selectedSessionId,
    setSelectedSessionId,
    joinedCampaignId,
    hubStatus,
    log,
    rollExpression,
    setRollExpression,
    actionInput,
    setActionInput,
    actionFeed,
    appendLog,
    // mutations
    createSession,
    createCharacter,
    rollDice,
    setAutonomyLevel,
    postAction,
    resolveProposal,
  };
}

"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import { queriesToRefresh, type AssistantHistory, type AssistantTurn } from "./assistant-view";

/**
 * ผู้ช่วย DM ช่องเดียว (แทนที่ปรึกษา + ช่องปรึกษา AI เดิม) — ถามข้อมูลได้ + สั่งงานหลายขั้น
 *
 * ประวัติและการ์ดที่รออนุมัติเป็นของ server ทั้งหมด (refresh แล้วยังอยู่ ข้ามวันก็ทำต่อได้) · ผูกกับเซสชันที่
 * เลือกอยู่ตอนส่งแต่ละข้อความ ไม่ใช่ผูกทั้งช่อง — ไม่เลือกเซสชันก็ยังคุยเตรียมเกมได้
 */
export function useAssistant(campaignId: string, sessionId: string | null) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();
  const queryKey = ["assistant", campaignId];

  const historyQuery = useQuery({
    queryKey,
    queryFn: () => apiFetch<AssistantHistory>(`/campaigns/${campaignId}/assistant`, { token }),
    enabled: !!token && !!campaignId,
    // DM-only — ผู้เล่นที่เปิดหน้านี้ได้ 403 เสมอ ไม่ต้องลองซ้ำ
    retry: false,
  });

  const afterTurn = async (turn: AssistantTurn | undefined) => {
    await queryClient.invalidateQueries({ queryKey });
    for (const key of queriesToRefresh(turn?.activity ?? [])) {
      await queryClient.invalidateQueries({ queryKey: [key] });
    }
  };

  const send = useMutation({
    mutationFn: (content: string) =>
      apiFetch<AssistantTurn>(`/campaigns/${campaignId}/assistant`, {
        method: "POST",
        token,
        body: { content, sessionId },
      }),
    onSettled: (turn) => afterTurn(turn),
  });

  const decide = useMutation({
    mutationFn: (input: { runId: string; toolUseId: string; approve: boolean; editedText?: string }) =>
      apiFetch<AssistantTurn>(`/campaigns/${campaignId}/assistant/runs/${input.runId}/decisions`, {
        method: "POST",
        token,
        body: { toolUseId: input.toolUseId, approve: input.approve, editedText: input.editedText },
      }),
    onSettled: (turn) => afterTurn(turn),
  });

  return {
    messages: historyQuery.data?.messages ?? [],
    pendingRun: historyQuery.data?.pendingRun ?? null,
    isLoading: historyQuery.isPending,
    isError: historyQuery.isError,
    error: historyQuery.error,
    send,
    decide,
  };
}

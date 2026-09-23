"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";

/**
 * แชทที่ปรึกษาส่วนตัวของ DM (Part C ของแผน "DM AI Assistant", 2026-09-22)
 *
 * **ต่างจาก `consult` ใน `use-campaign-workspace.ts` ตรงที่ผูกกับ "แคมเปญ" ไม่ใช่ "session"** — ใช้ได้
 * ตั้งแต่ยังไม่เปิดเซสชันเลย (เตรียมตัวก่อนเล่น / วางแผนตอนถัดไป) จึงไม่ต้องรอ `selectedSessionId`
 * เหมือนช่องปรึกษาเดิม · ฝั่ง API ก็เก็บคนละตาราง (`consult_messages` ไม่ใช่ `messages`) และ
 * **ไม่มี tool ให้ AI เรียกเลย** — คุยได้อย่างเดียว ไม่ทอยเต๋า ไม่แก้ state ของเกม
 *
 * ประวัติบทสนทนาเป็นของ server (ไม่ใช่ state ในหน้าจอแบบ `consultMessages`) เพราะต้องรอดข้ามการ
 * refresh — DM เตรียมเซสชันวันนี้ แล้วเปิดมาอ่านต่อพรุ่งนี้ได้
 */

export type AdvisorRole = "Dm" | "Ai";

export type AdvisorMessage = {
  id: string;
  role: AdvisorRole | string;
  content: string;
  linkedBeatId: string | null;
  createdAt: string;
};

type AdvisorReply = {
  messageId: string;
  content: string;
  linkedBeatId: string | null;
};

export function useAdvisor(campaignId: string) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();
  const queryKey = ["advisor", campaignId];

  const historyQuery = useQuery({
    queryKey,
    queryFn: () => apiFetch<AdvisorMessage[]>(`/campaigns/${campaignId}/advisor`, { token }),
    enabled: !!token && !!campaignId,
    // DM-only — ผู้เล่นที่เปิดหน้านี้จะได้ 403 ตลอด ไม่ต้องลองซ้ำให้เปลือง
    retry: false,
  });

  const send = useMutation({
    mutationFn: (content: string) =>
      apiFetch<AdvisorReply>(`/campaigns/${campaignId}/advisor`, {
        method: "POST",
        token,
        body: { content },
      }),
    // server เป็นเจ้าของประวัติทั้งเส้น (ทั้งข้อความ DM และคำตอบ AI ถูกเขียนที่นั่น) — ดึงใหม่ทีเดียว
    // แทนที่จะปะ cache เองสองรอบให้เสี่ยงลำดับเพี้ยน
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey });
    },
  });

  return {
    messages: historyQuery.data ?? [],
    isLoading: historyQuery.isPending,
    isError: historyQuery.isError,
    error: historyQuery.error,
    send,
  };
}

"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";

/**
 * บันทึกแคมเปญ (Part D ของแผน "DM AI Assistant") — DM-only ทุก endpoint
 *
 * ที่มาของแต่ละรายการ (`authoredBy`): `Dm` พิมพ์เอง · `Ai` สั่งให้ AI สรุปเซสชัน · `System` ระบบเขียนเอง
 * ตอนจบ encounter (สรุปว่ามอนสเตอร์ตัวไหนล้ม) — AI ค้นบันทึกนี้ได้ผ่าน tool ตอน DM ปรึกษา
 */

export type JournalAuthor = "Dm" | "Ai" | "System";

export type JournalEntry = {
  id: string;
  sessionId: string | null;
  title: string;
  content: string;
  authoredBy: JournalAuthor | string;
  createdAt: string;
};

export function useJournal(campaignId: string) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();
  const queryKey = ["journal", campaignId];
  const invalidate = () => queryClient.invalidateQueries({ queryKey });

  const listQuery = useQuery({
    queryKey,
    queryFn: () => apiFetch<JournalEntry[]>(`/campaigns/${campaignId}/journal`, { token }),
    enabled: !!token && !!campaignId,
    retry: false,
  });

  const create = useMutation({
    mutationFn: (input: { title: string; content: string; sessionId: string | null }) =>
      apiFetch<JournalEntry>(`/campaigns/${campaignId}/journal`, { method: "POST", token, body: input }),
    onSuccess: invalidate,
  });

  const aiSummary = useMutation({
    mutationFn: (sessionId: string) =>
      apiFetch<JournalEntry>(`/campaigns/${campaignId}/journal/ai-summary`, {
        method: "POST",
        token,
        body: { sessionId },
      }),
    onSuccess: invalidate,
  });

  const update = useMutation({
    mutationFn: ({ id, title, content }: { id: string; title: string; content: string }) =>
      apiFetch<JournalEntry>(`/campaigns/${campaignId}/journal/${id}`, {
        method: "PUT",
        token,
        body: { title, content },
      }),
    onSuccess: invalidate,
  });

  const remove = useMutation({
    mutationFn: (id: string) =>
      apiFetch<null>(`/campaigns/${campaignId}/journal/${id}`, { method: "DELETE", token }),
    onSuccess: invalidate,
  });

  return {
    entries: listQuery.data ?? [],
    isLoading: listQuery.isPending,
    isError: listQuery.isError,
    error: listQuery.error,
    create,
    aiSummary,
    update,
    remove,
  };
}

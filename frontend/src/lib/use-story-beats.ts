"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { API_BASE_URL, ApiError, apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import type { StoryBeat } from "./story-beat-view";

/**
 * เนื้อเรื่องของแคมเปญ (`/campaigns/{id}/beats`) + นำเข้าจากเอกสาร (`/story-imports`) — DM-only ทุก endpoint
 *
 * ⚠️ การอัปโหลดเป็น multipart ใช้ `apiFetch` ไม่ได้ (ตั้ง Content-Type เป็น JSON เองจน boundary หาย) จึงเรียก
 * `fetch` ตรงแบบเดียวกับ `use-scene-setup.ts`
 */

export type BeatInput = {
  title: string;
  summary: string;
  objective: string;
  entryTrigger: string;
  exitConditions: string[];
  dmNotes: string;
  secrets: string;
};

export type StoryImportResult = {
  documents: { id: string; fileName: string }[];
  beats: StoryBeat[];
};

export function useStoryBeats(campaignId: string) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();
  const queryKey = ["story-beats", campaignId];
  const invalidate = () => queryClient.invalidateQueries({ queryKey });

  const listQuery = useQuery({
    queryKey,
    queryFn: () => apiFetch<StoryBeat[]>(`/campaigns/${campaignId}/beats`, { token }),
    enabled: !!token && !!campaignId,
    retry: false,
  });

  const create = useMutation({
    mutationFn: (input: BeatInput) =>
      apiFetch<StoryBeat>(`/campaigns/${campaignId}/beats`, { method: "POST", token, body: input }),
    onSuccess: invalidate,
  });

  const update = useMutation({
    mutationFn: ({ id, ...input }: BeatInput & { id: string }) =>
      apiFetch<StoryBeat>(`/campaigns/${campaignId}/beats/${id}`, { method: "PATCH", token, body: input }),
    onSuccess: invalidate,
  });

  const remove = useMutation({
    mutationFn: (id: string) => apiFetch<null>(`/campaigns/${campaignId}/beats/${id}`, { method: "DELETE", token }),
    onSuccess: invalidate,
  });

  const reorder = useMutation({
    mutationFn: (beatIds: string[]) =>
      apiFetch<StoryBeat[]>(`/campaigns/${campaignId}/beats/order`, { method: "PATCH", token, body: { beatIds } }),
    onSuccess: invalidate,
  });

  const approve = useMutation({
    mutationFn: async (ids: string[]) => {
      // ทีละตัวตามลำดับ — endpoint รับทีละ beat และ DM มักกด "ยืนยันทั้งหมด" หลังนำเข้าไม่กี่สิบฉาก
      for (const id of ids) {
        await apiFetch<StoryBeat>(`/campaigns/${campaignId}/beats/${id}/approve`, { method: "POST", token });
      }
    },
    onSettled: invalidate,
  });

  const setActive = useMutation({
    mutationFn: ({ sessionId, beatId }: { sessionId: string; beatId: string }) =>
      apiFetch(`/campaigns/${campaignId}/sessions/${sessionId}/active-beat`, {
        method: "POST",
        token,
        body: { beatId },
      }),
    onSuccess: invalidate,
  });

  const importFiles = useMutation({
    mutationFn: async (files: File[]) => {
      const form = new FormData();
      for (const file of files) form.append("files", file);
      const response = await fetch(`${API_BASE_URL}/campaigns/${campaignId}/story-imports`, {
        method: "POST",
        headers: token ? { Authorization: `Bearer ${token}` } : {},
        body: form,
      });
      const text = await response.text();
      const body: unknown = text ? JSON.parse(text) : null;
      if (!response.ok) throw new ApiError(response.status, body);
      return body as StoryImportResult;
    },
    onSuccess: invalidate,
  });

  return {
    beats: listQuery.data ?? [],
    isLoading: listQuery.isPending,
    isError: listQuery.isError,
    error: listQuery.error,
    create,
    update,
    remove,
    reorder,
    approve,
    setActive,
    importFiles,
  };
}

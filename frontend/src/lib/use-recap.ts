"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";

/**
 * สรุปเซสชัน — แถวเดียวมีสองฉบับ: `dmVersionText` (ของ DM + ความจำของ AI, มีความลับได้) และ
 * `playerVersionText` ("ตอนที่แล้ว…" ให้ผู้เล่นอ่าน)
 *
 * server เป็นคนตัดว่าใครเห็นอะไร — ผู้เล่นได้ `dmVersionText: null` เสมอ หน้าจอไม่ต้อง (และไม่ควร) ตัดเอง
 */

export type Recap = {
  sessionId: string;
  dmVersionText: string | null;
  playerVersionText: string;
  createdAt: string;
};

/** ฝั่ง DM — recap ของเซสชันที่เลือก + สร้างด้วย AI + แก้เอง */
export function useSessionRecap(campaignId: string, sessionId: string | null) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();
  const queryKey = ["recap", campaignId, sessionId];

  const recapQuery = useQuery({
    queryKey,
    // 204 (ยังไม่มี recap) → apiFetch คืน null
    queryFn: () =>
      apiFetch<Recap | null>(`/campaigns/${campaignId}/sessions/${sessionId}/recap`, { token }),
    enabled: !!token && !!campaignId && !!sessionId,
  });

  const onChanged = async () => {
    await queryClient.invalidateQueries({ queryKey });
    await queryClient.invalidateQueries({ queryKey: ["recap-latest", campaignId] });
  };

  const generate = useMutation({
    mutationFn: () =>
      apiFetch<Recap>(`/campaigns/${campaignId}/sessions/${sessionId}/recap/generate`, {
        method: "POST",
        token,
      }),
    onSuccess: onChanged,
  });

  const save = useMutation({
    mutationFn: (input: { dmVersionText: string; playerVersionText: string }) =>
      apiFetch<Recap>(`/campaigns/${campaignId}/sessions/${sessionId}/recap`, {
        method: "PUT",
        token,
        body: input,
      }),
    onSuccess: onChanged,
  });

  return { recap: recapQuery.data ?? null, isLoading: recapQuery.isPending, generate, save };
}

/** ฝั่งผู้เล่น — "ตอนที่แล้ว…" ล่าสุดของแคมเปญ (server ข้าม recap ที่ฉบับผู้เล่นว่างให้แล้ว) */
export function useLatestRecap(campaignId: string) {
  const token = useAuthStore((s) => s.token);
  const query = useQuery({
    queryKey: ["recap-latest", campaignId],
    queryFn: () => apiFetch<Recap | null>(`/campaigns/${campaignId}/recaps/latest`, { token }),
    enabled: !!token && !!campaignId,
  });
  return { recap: query.data ?? null };
}

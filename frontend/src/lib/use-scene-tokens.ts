"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";

/**
 * Sprint H Part 2 — วาง/ย้าย/ลบ token บนแผนที่ (เครื่องมือของ DM)
 *
 * **แยกจาก `useScene` โดยตั้งใจ** — `useScene` ถูกใช้โดย `TableView` ด้วย และจอกลางต้องไม่มี
 * mutation ติดมือเลยตาม design doc §13.1 ("ช่อง Input ของจอกลาง = ไม่มีเลย") · เอา mutation
 * ชุดนี้ไปไว้ในนั้นจะทำให้จอกลางถือเครื่องมือที่ตัวเองไม่มีสิทธิ์ใช้ ซึ่งเป็นเหตุผลเดียวกับที่
 * `TableView` เลี่ยง `useCampaignWorkspace` มาตั้งแต่ Sprint G Part 6
 *
 * ⚠️ endpoint ชุดนี้ผูกกับ **sceneId** ไม่ใช่ sessionId (ต่างจาก movement/*) — ดู
 * `SceneTokenEndpoints.cs`
 *
 * นี่คือเส้นทาง **"วาง/ย้ายอิสระ"** ตาม design doc §14.2 — persist ทันทีตอนวาง ไม่คิดระยะ
 * ไม่เกิด OA · การเดินที่คิดกฎจริงเป็นคนละเส้นทาง (`movement/commit` ใน `use-movement.ts`)
 */
export function useSceneTokens(campaignId: string, sceneId: string | null) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ["scene"] });
    // ขอบเขตที่เดินได้คิดจากตำแหน่งปัจจุบัน — ย้าย/วาง/ลบ token แล้วต้องคำนวณใหม่
    await queryClient.invalidateQueries({ queryKey: ["reachable"] });
    await queryClient.invalidateQueries({ queryKey: ["affordances"] });
  };

  const placeToken = useMutation({
    mutationFn: ({ entityType, entityId, x, y }: {
      entityType: "Character" | "Monster";
      entityId: string;
      x: number;
      y: number;
    }) =>
      apiFetch<{ id: string }>(`/campaigns/${campaignId}/scenes/${sceneId}/tokens`, {
        method: "POST",
        token,
        body: { entityType, entityId, x, y },
      }),
    onSuccess: refresh,
  });

  const moveToken = useMutation({
    mutationFn: ({ tokenId, x, y }: { tokenId: string; x: number; y: number }) =>
      apiFetch<{ id: string }>(`/campaigns/${campaignId}/scenes/${sceneId}/tokens/${tokenId}`, {
        method: "PATCH",
        token,
        body: { x, y },
      }),
    onSuccess: refresh,
  });

  const removeToken = useMutation({
    mutationFn: (tokenId: string) =>
      apiFetch<unknown>(`/campaigns/${campaignId}/scenes/${sceneId}/tokens/${tokenId}`, {
        method: "DELETE",
        token,
      }),
    onSuccess: refresh,
  });

  return { placeToken, moveToken, removeToken };
}

"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import type { CharacterPreset } from "./character-preset-types";

/**
 * Sprint G Part 7 — คลัง preset ตัวละคร (design doc §5) + สร้างตัวละครจาก preset ที่เลือก
 *
 * ไม่ใช่ DM-only — ผู้เล่นเลือกของตัวเองได้เสมอ (player-side-design §18)
 */
export function useCharacterPresets(campaignId: string) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const presetsQuery = useQuery({
    queryKey: ["character-presets", campaignId],
    queryFn: () => apiFetch<CharacterPreset[]>(`/campaigns/${campaignId}/character-presets`, { token }),
    enabled: !!token && !!campaignId,
  });

  const createFromPreset = useMutation({
    mutationFn: ({ name, presetId }: { name: string; presetId: string }) =>
      apiFetch<{ id: string }>(`/campaigns/${campaignId}/characters`, {
        method: "POST",
        token,
        body: { name, presetId },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["characters", campaignId] });
      // popularityCount ของ preset ที่เพิ่งใช้เปลี่ยนไป (+1) — ดึงลิสต์ preset ใหม่ให้ลำดับตรง
      await queryClient.invalidateQueries({ queryKey: ["character-presets", campaignId] });
    },
  });

  return {
    presets: presetsQuery.data ?? [],
    isLoading: presetsQuery.isPending,
    isError: presetsQuery.isError,
    createFromPreset,
  };
}

"use client";

import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import type { AffordanceList } from "./affordance-types";

/**
 * Sprint G Part 9 — "ตาฉันแล้ว ทำอะไรได้บ้าง" ที่ server คำนวณมาแล้ว
 *
 * แยกเป็น hook ของตัวเองแทนที่จะยัดลง `useCampaignWorkspace` เพราะผูกกับ **ตัวละครหนึ่งตัว**
 * (ผู้เล่นที่คุมหลายตัวจะมีลิสต์คนละชุด) และเพราะ TableView/หน้าอื่นไม่ควรดึงมันติดมาโดยไม่ตั้งใจ
 *
 * ผลลัพธ์เปลี่ยนได้จากหลายทางที่ hook นี้ไม่รู้ตัว — ศัตรูขยับ, DM เปิดหมอก, มีคนใช้ action ไป —
 * จึงตั้ง `staleTime: 0` ให้ทุกการ invalidate ของ scene/turn ดึงใหม่จริง แทนการ cache ไว้เงียบ ๆ
 * แล้วโชว์ตัวเลือกที่ไม่ตรงกับสถานะจริงบนโต๊ะ
 */
export function useAffordances(campaignId: string, sessionId: string | null, characterId: string | null) {
  const token = useAuthStore((s) => s.token);

  const query = useQuery({
    queryKey: ["affordances", campaignId, sessionId, characterId],
    queryFn: () =>
      apiFetch<AffordanceList>(
        `/campaigns/${campaignId}/sessions/${sessionId}/affordances/${characterId}`,
        { token },
      ),
    enabled: !!token && !!campaignId && !!sessionId && !!characterId,
    staleTime: 0,
  });

  return {
    options: query.data?.options ?? [],
    isLoading: query.isPending,
    isError: query.isError,
  };
}

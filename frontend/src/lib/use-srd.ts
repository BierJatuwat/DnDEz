"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import type {
  CampaignMonster,
  CopySrdMonsterResult,
  SrdCorpusItem,
  SrdListResponse,
  SrdMonsterDetail,
  SrdMonsterSummary,
} from "./srd-types";

/**
 * คลัง SRD 5.2.1 + แม่แบบมอนสเตอร์ของแคมเปญ
 *
 * **ค่า `attribution` มาจาก server ทุก response** — ห้าม hardcode ข้อความสัญญาอนุญาตไว้ฝั่ง frontend
 * เพราะ CC-BY-4.0 บังคับถ้อยคำที่ต้องแสดง ถ้ามีสองสำเนาแล้ววันหนึ่งไม่ตรงกัน ฝั่งที่ผิดคือฝั่งที่ผู้ใช้เห็น
 */

/** จำนวนรายการต่อหน้า — 24 ลงตัวกับกริด 2/3/4 คอลัมน์ทุกขนาดจอที่ใช้จริง */
export const MONSTER_PAGE_SIZE = 24;

export function useSrdMonsters(filters: { query: string; crMax: string; page: number }) {
  const token = useAuthStore((s) => s.token);
  const params = new URLSearchParams();
  if (filters.query.trim()) params.set("query", filters.query.trim());
  if (filters.crMax) params.set("crMax", filters.crMax);
  params.set("limit", String(MONSTER_PAGE_SIZE));
  params.set("offset", String(filters.page * MONSTER_PAGE_SIZE));

  const query = useQuery({
    queryKey: ["srd-monsters", filters.query.trim(), filters.crMax, filters.page],
    queryFn: () =>
      apiFetch<SrdListResponse<SrdMonsterSummary>>(`/srd/monsters?${params.toString()}`, { token }),
    enabled: !!token,
    // คลังนี้เป็นข้อมูลนิ่ง (เปลี่ยนเฉพาะตอน migrate) — ไม่ต้องดึงซ้ำระหว่างที่ DM กดดูไปมา
    staleTime: 5 * 60 * 1000,
    // ระหว่างพิมพ์ค้นหา/เปลี่ยนหน้า ให้คงผลเดิมไว้ก่อน ไม่งั้นลิสต์กะพริบเป็นช่องว่างทุกตัวอักษรที่พิมพ์
    placeholderData: (previous) => previous,
  });

  return {
    monsters: query.data?.items ?? [],
    total: query.data?.total ?? 0,
    attribution: query.data?.attribution ?? null,
    isLoading: query.isPending,
    isError: query.isError,
  };
}

export function useSrdMonsterDetail(key: string | null) {
  const token = useAuthStore((s) => s.token);
  const query = useQuery({
    queryKey: ["srd-monster", key],
    queryFn: () => apiFetch<SrdMonsterDetail>(`/srd/monsters/${key}`, { token }),
    enabled: !!token && !!key,
    staleTime: 5 * 60 * 1000,
  });

  return { detail: query.data ?? null, isLoading: query.isPending && !!key, isError: query.isError };
}

export function useSrdCorpus(filters: { category: string; query: string }) {
  const token = useAuthStore((s) => s.token);
  const params = new URLSearchParams();
  if (filters.category) params.set("category", filters.category);
  if (filters.query.trim()) params.set("query", filters.query.trim());
  params.set("limit", "100");

  const query = useQuery({
    queryKey: ["srd-corpus", filters.category, filters.query.trim()],
    queryFn: () => apiFetch<SrdListResponse<SrdCorpusItem>>(`/srd/corpus?${params.toString()}`, { token }),
    enabled: !!token,
    staleTime: 5 * 60 * 1000,
    placeholderData: (previous) => previous,
  });

  return {
    items: query.data?.items ?? [],
    total: query.data?.total ?? 0,
    attribution: query.data?.attribution ?? null,
    isLoading: query.isPending,
    isError: query.isError,
  };
}

/** เนื้อหาเต็มของรายการในคลังกฎ (เวท/สถานะ/กฎ) — ใช้ตอนกางรายการที่เลือก */
export function useSrdCorpusDetail(category: string | null, key: string | null) {
  const token = useAuthStore((s) => s.token);
  const query = useQuery({
    queryKey: ["srd-corpus-entry", category, key],
    queryFn: () => apiFetch<{ content: string }>(`/srd/corpus/${category}/${key}`, { token }),
    enabled: !!token && !!category && !!key,
    staleTime: 5 * 60 * 1000,
  });

  return { content: query.data?.content ?? null, isLoading: query.isPending && !!key };
}

/**
 * แม่แบบมอนสเตอร์ที่อยู่ในแคมเปญแล้ว + การคัดลอกจากคลัง SRD เข้ามา
 *
 * **DM-only ทั้งคู่ที่ชั้น API** — `enabled` ตรงนี้กันแค่ไม่ให้ยิง request ที่รู้อยู่แล้วว่าจะได้ 403
 * ไม่ใช่ตัวกันสิทธิ์ (หน้าจอซ่อนปุ่มเป็นเรื่อง UX เสมอ ไม่ใช่ security — ดู `useCampaignRole`)
 */
export function useCampaignMonsters(campaignId: string, enabled = true) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const monstersQuery = useQuery({
    queryKey: ["campaign-monsters", campaignId],
    queryFn: () => apiFetch<CampaignMonster[]>(`/campaigns/${campaignId}/monsters`, { token }),
    enabled: !!token && !!campaignId && enabled,
  });

  const copyFromSrd = useMutation({
    mutationFn: (key: string) =>
      apiFetch<CopySrdMonsterResult>(`/campaigns/${campaignId}/monsters/from-srd`, {
        method: "POST",
        token,
        body: { key },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["campaign-monsters", campaignId] });
    },
  });

  /**
   * ปรับค่าพลังของ **แม่แบบ** — ส่งเฉพาะช่องที่แก้ (PATCH ไม่ใช่ PUT)
   *
   * ⚠️ มีผลกับทุกตัวที่วางจากแม่แบบนี้ เพราะมอนสเตอร์ยังไม่มี HP รายตัว (ดู doc comment ของ
   * `MonsterEndpoints` ฝั่ง C#) — หน้าจอต้องบอก DM ให้ชัด ไม่ใช่ปล่อยให้เดา
   */
  const updateMonster = useMutation({
    mutationFn: ({ monsterId, ...changes }: MonsterStatChanges & { monsterId: string }) =>
      apiFetch<CampaignMonster>(`/campaigns/${campaignId}/monsters/${monsterId}`, {
        method: "PATCH",
        token,
        body: changes,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["campaign-monsters", campaignId] });
      // ค่าที่แก้ (reach/speed) เป็นข้อมูลที่ engine ใช้คำนวณ — ขอบเขตการเดินและตัวเลือกที่คำนวณไว้แล้ว
      // อาจเปลี่ยนตาม ต้องให้ server คิดใหม่ ไม่ใช่โชว์ค่าที่คิดจากตัวเลขเก่าต่อไป
      await queryClient.invalidateQueries({ queryKey: ["reachable"] });
      await queryClient.invalidateQueries({ queryKey: ["affordances"] });
    },
  });

  return {
    monsters: monstersQuery.data ?? [],
    isLoading: monstersQuery.isPending && enabled,
    isError: monstersQuery.isError,
    copyFromSrd,
    updateMonster,
  };
}

/** ช่องที่ DM แก้ได้ — ไม่ส่ง = ไม่แก้ */
export type MonsterStatChanges = {
  name?: string;
  armorClass?: number;
  hitPoints?: number;
  speedFeet?: number;
  reachFeet?: number;
};

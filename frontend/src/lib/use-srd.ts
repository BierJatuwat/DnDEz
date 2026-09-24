"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import { useSrdLanguageStore } from "./srd-language-store";
import type {
  CampaignMonster,
  CopySrdMonsterResult,
  SpawnedMonster,
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
 *
 * **ปุ่มสลับภาษา (2026-09-22)**: ทุก hook ในไฟล์นี้อ่านภาษาปัจจุบันจาก `useSrdLanguageStore` เองแล้วต่อ
 * `?lang=` ให้อัตโนมัติ — component ที่เรียก hook พวกนี้ไม่ต้องรู้เรื่องภาษาเลย สลับปุ่มที่ไหนก็ได้ผลทันที
 * เพราะภาษาอยู่ใน query key: TanStack Query แคชสองภาษาแยกกัน สลับไปมาไม่ต้องรอโหลดซ้ำ (แคชแล้วเร็วขึ้นเรื่อย ๆ)
 */

/** จำนวนต่อหน้าที่ DM เลือกได้ — backend clamp ไว้ที่ 1–100 (`SrdEndpoints.MaxLimit`) ตัวเลือกห้ามเกินนั้น */
export const MONSTER_PAGE_SIZE_OPTIONS = [10, 25, 50] as const;
export const DEFAULT_MONSTER_PAGE_SIZE = 10;

export function useSrdMonsters(filters: { query: string; crMax: string; page: number; pageSize: number }) {
  const token = useAuthStore((s) => s.token);
  const lang = useSrdLanguageStore((s) => s.language);
  const params = new URLSearchParams();
  if (filters.query.trim()) params.set("query", filters.query.trim());
  if (filters.crMax) params.set("crMax", filters.crMax);
  params.set("lang", lang);
  params.set("limit", String(filters.pageSize));
  params.set("offset", String(filters.page * filters.pageSize));

  const query = useQuery({
    queryKey: ["srd-monsters", filters.query.trim(), filters.crMax, filters.page, filters.pageSize, lang],
    queryFn: () =>
      apiFetch<SrdListResponse<SrdMonsterSummary>>(`/srd/monsters?${params.toString()}`, { token }),
    enabled: !!token,
    // คลังนี้เป็นข้อมูลนิ่ง (เปลี่ยนเฉพาะตอน migrate) — ไม่ต้องดึงซ้ำระหว่างที่ DM กดดูไปมา
    staleTime: 5 * 60 * 1000,
    // ระหว่างพิมพ์ค้นหา/เปลี่ยนหน้า/สลับภาษา ให้คงผลเดิมไว้ก่อน ไม่งั้นลิสต์กะพริบเป็นช่องว่างทุกครั้ง
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
  const lang = useSrdLanguageStore((s) => s.language);
  const query = useQuery({
    queryKey: ["srd-monster", key, lang],
    queryFn: () => apiFetch<SrdMonsterDetail>(`/srd/monsters/${key}?lang=${lang}`, { token }),
    enabled: !!token && !!key,
    staleTime: 5 * 60 * 1000,
  });

  return { detail: query.data ?? null, isLoading: query.isPending && !!key, isError: query.isError };
}

export function useSrdCorpus(filters: { category: string; query: string }) {
  const token = useAuthStore((s) => s.token);
  const lang = useSrdLanguageStore((s) => s.language);
  const params = new URLSearchParams();
  if (filters.category) params.set("category", filters.category);
  if (filters.query.trim()) params.set("query", filters.query.trim());
  params.set("lang", lang);
  params.set("limit", "100");

  const query = useQuery({
    queryKey: ["srd-corpus", filters.category, filters.query.trim(), lang],
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
  const lang = useSrdLanguageStore((s) => s.language);
  const query = useQuery({
    queryKey: ["srd-corpus-entry", category, key, lang],
    queryFn: () =>
      // `contentTranslated` เป็น null จาก server เองเมื่อ lang=en (ดู doc comment ของ endpoint ฝั่ง C#
      // — ไม่มีแนวคิด "แปลแล้วหรือยัง" ตอนขอภาษาอังกฤษซึ่งเป็นต้นฉบับอยู่แล้ว) ไม่ต้องเติม logic ซ้ำที่นี่
      apiFetch<{ content: string; contentTranslated?: boolean | null }>(
        `/srd/corpus/${category}/${key}?lang=${lang}`,
        { token },
      ),
    enabled: !!token && !!category && !!key,
    staleTime: 5 * 60 * 1000,
  });

  return {
    content: query.data?.content ?? null,
    contentTranslated: query.data?.contentTranslated ?? undefined,
    isLoading: query.isPending && !!key,
  };
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
   * ⚠️ มีผลกับ token มอนสเตอร์ที่วางจากแม่แบบนี้ตรง ๆ ทุกตัว (ใช้ค่าชุดเดียวกัน) · ตัวที่ **spawn** ไปแล้ว
   * (`spawnMonster` ด้านล่าง) ไม่เปลี่ยนตาม เพราะคัดลอกค่าไว้ตอน spawn — หน้าจอต้องบอก DM ให้ชัด
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

  /**
   * ส่งมอนสเตอร์ลงสนาม — สร้างตัวละคร DM-controlled N ตัวที่มี HP ของตัวเอง เข้าวง initiative ได้
   * (เลือกได้ในแผง Encounter) และถูกลบพร้อมสรุปผลลงบันทึกแคมเปญตอนจบ encounter
   */
  const spawnMonster = useMutation({
    mutationFn: ({
      monsterId,
      count,
      statsVisibleToParty,
    }: {
      monsterId: string;
      count: number;
      statsVisibleToParty: boolean;
    }) =>
      apiFetch<SpawnedMonster[]>(`/campaigns/${campaignId}/monsters/${monsterId}/spawn`, {
        method: "POST",
        token,
        body: { count, statsVisibleToParty },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["characters", campaignId] });
    },
  });

  return {
    monsters: monstersQuery.data ?? [],
    isLoading: monstersQuery.isPending && enabled,
    isError: monstersQuery.isError,
    copyFromSrd,
    updateMonster,
    spawnMonster,
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

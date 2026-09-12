"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch, ApiError } from "./api";
import { useAuthStore } from "./auth-store";
import type { FogArea, Scene } from "./scene-types";
import type { SceneTerrain } from "./terrain-paint";

/** ค่าคงที่ตัวเดียว (ไม่สร้าง object ใหม่ทุก render) — ฉากที่ยังไม่มีพื้นพิเศษ หรือ server รุ่นเก่า */
const EMPTY_TERRAIN: SceneTerrain = { difficult: [], blocked: [] };

/**
 * Sprint G Part 6 — ดึง scene ที่ active ของเซสชัน + คำสั่งเปิด/ปิดพื้นที่หมอก (DM เท่านั้น)
 *
 * แยกเป็น hook ของตัวเองไม่รวมเข้า `useCampaignWorkspace` เพราะ **จอกลาง (TableView) ต้องใช้ scene
 * โดยไม่ต้องแตะของอื่นเลย** (ไม่มี mutation ไม่มี consult ไม่มี proposal — ดู design doc §13.1 ที่
 * ช่อง Input ของจอกลางคือ "ไม่มีเลย") ถ้ายัดรวมเข้า workspace hub เดิม จอกลางจะลาก mutation ที่
 * ตัวเองไม่มีสิทธิ์ใช้ติดมาทั้งชุด
 *
 * ⚠️ payload ที่ได้ **ถูกกรองตาม role มาแล้วจาก server** (ดู scene-types.ts) — hook นี้ไม่กรองอะไรซ้ำ
 * และห้ามกรอง เพราะการกรองซ้ำฝั่ง client จะบังความจริงว่า server ส่งอะไรมาให้จริง ๆ (ถ้า server
 * รั่ว เราต้องเห็นว่ามันรั่ว ไม่ใช่ให้ client ปิดทับไว้)
 */
export function useScene(campaignId: string, sessionId: string | null) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const sceneQuery = useQuery({
    queryKey: ["scene", sessionId],
    queryFn: () =>
      apiFetch<Scene>(`/campaigns/${campaignId}/sessions/${sessionId}/scene`, { token }),
    enabled: !!token && !!campaignId && !!sessionId,
    // 404 = เซสชันนี้ยังไม่มี scene ที่ active (ปกติมาก ไม่ใช่ error ที่ควร retry รัว ๆ)
    retry: false,
  });

  const scene = sceneQuery.data ?? null;

  /** 404 จาก endpoint นี้แปลว่า "ยังไม่มีแผนที่ในเซสชันนี้" ไม่ใช่ "โหลดพัง" — คนละข้อความบนจอ */
  const noScene =
    sceneQuery.isError &&
    sceneQuery.error instanceof ApiError &&
    sceneQuery.error.status === 404;

  const revealFog = useMutation({
    mutationFn: (area: { x: number; y: number; radius: number }) =>
      apiFetch<FogArea>(`/campaigns/${campaignId}/scenes/${scene?.id}/fog/reveal`, {
        method: "POST",
        token,
        body: area,
      }),
    onSuccess: () => {
      // broadcast FogChanged จะมาถึงเองด้วย แต่ invalidate ตรงนี้ทำให้คนที่กดเห็นผลทันทีโดยไม่ต้อง
      // รอ round-trip ของ SignalR (และยังถูกต้องถ้า hub หลุดอยู่พอดี)
      void queryClient.invalidateQueries({ queryKey: ["scene", sessionId] });
    },
  });

  const hideFog = useMutation({
    mutationFn: (areaId: string) =>
      apiFetch<unknown>(`/campaigns/${campaignId}/scenes/${scene?.id}/fog/hide`, {
        method: "POST",
        token,
        body: { areaId },
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["scene", sessionId] });
    },
  });

  return {
    scene,
    tokens: scene?.tokens ?? [],
    revealedAreas: scene?.revealedAreas ?? [],
    terrain: scene?.terrain ?? EMPTY_TERRAIN,
    isLoading: sceneQuery.isPending && !!sessionId,
    noScene,
    isError: sceneQuery.isError && !noScene,
    revealFog,
    hideFog,
  };
}

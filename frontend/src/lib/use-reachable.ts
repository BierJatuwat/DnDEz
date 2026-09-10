"use client";

import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import type { PathPoint } from "./use-movement";

export interface ReachableSquare {
  x: number;
  y: number;
  costFeet: number;
  /** เส้นทางที่ถูกที่สุดมาถึงช่องนี้ — server คำนวณให้แล้ว client ไม่ต้องคิดเอง */
  path: PathPoint[];
}

export interface ReachableArea {
  characterId: string;
  budgetFeet: number;
  squares: ReachableSquare[];
}

/**
 * Sprint G Part 10 — "ลากไปได้ถึงไหน" ที่ server คำนวณตามกฎจริง
 *
 * §3.1: ขอบเขตนี้เป็น **การกระจายตามต้นทุน ไม่ใช่วงรัศมี** — ถ้ามีพื้นเดินยากขวาง ช่องที่ไกลเท่ากัน
 * จะไปถึงได้ไม่เท่ากัน · client แค่วาดสิ่งที่ได้มา ห้ามคำนวณขอบเขตเอง
 *
 * เส้นทางติดมากับทุกช่อง เพื่อให้ตอนปล่อย token ส่งเส้นทางของ server กลับไป commit ได้ตรง ๆ —
 * ถ้าให้ client ประกอบเส้นทางเอง จะเปิดช่องให้ส่งทางที่เลี่ยง OA หรือลัดผ่านสิ่งกีดขวางเข้ามา
 */
export function useReachable(campaignId: string, sessionId: string | null, characterId: string | null) {
  const token = useAuthStore((s) => s.token);

  const query = useQuery({
    queryKey: ["reachable", campaignId, sessionId, characterId],
    queryFn: () =>
      apiFetch<ReachableArea>(
        `/campaigns/${campaignId}/sessions/${sessionId}/movement/reachable/${characterId}`,
        { token },
      ),
    enabled: !!token && !!campaignId && !!sessionId && !!characterId,
    staleTime: 0,
  });

  return {
    squares: query.data?.squares ?? [],
    budgetFeet: query.data?.budgetFeet ?? 0,
  };
}

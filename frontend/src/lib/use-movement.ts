"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";

export interface MovementWarning {
  threatId: string;
  threatName: string;
  leavesAtStepIndex: number;
  attackFromX: number;
  attackFromY: number;
  message: string;
}

export interface MovementPreview {
  characterId: string;
  costFeet: number;
  isAffordable: boolean;
  requiresLandingCheck: boolean;
  warnings: MovementWarning[];
}

export interface MovementCommitResult {
  characterId: string;
  status: "Completed" | "AwaitingReaction";
  costFeet: number;
  pendingReactionId: string | null;
  awaitingThreatId: string | null;
  awaitingThreatName: string | null;
  reactionTimeoutSeconds: number | null;
  reactionUsed: boolean;
}

export interface PathPoint {
  x: number;
  y: number;
}

/**
 * Sprint G Part 10 (ส่วน ง) — เดินบนแผนที่: ดูราคา+คำเตือนก่อน แล้วค่อยกดจริง
 *
 * `preview` กับ `commit` ใช้เส้นทางชุดเดียวกันเสมอ — server คำนวณด้วยโค้ดชุดเดียวกันทั้งสองทาง
 * (`MovementSceneContext`) ผู้เล่นจึงไม่มีทางเห็นคำเตือนอย่างหนึ่งแล้วโดนอีกอย่างตอนกด
 */
export function useMovement(campaignId: string, sessionId: string | null) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const refreshScene = async () => {
    await queryClient.invalidateQueries({ queryKey: ["scene"] });
    await queryClient.invalidateQueries({ queryKey: ["affordances"] });
    // ขอบเขตที่เดินได้คิดจากช่องที่ยืนอยู่ — ขยับแล้วต้องคำนวณใหม่จากจุดใหม่ ไม่งั้นครั้งต่อไป
    // ผู้เล่นจะเห็นขอบเขตของตำแหน่งเก่าแล้วลากไปวางในที่ที่ commit จะปฏิเสธ
    await queryClient.invalidateQueries({ queryKey: ["reachable"] });
  };

  const preview = useMutation({
    mutationFn: ({ characterId, path, disengage }: {
      characterId: string;
      path: PathPoint[];
      disengage: boolean;
    }) =>
      apiFetch<MovementPreview>(`/campaigns/${campaignId}/sessions/${sessionId}/movement/preview`, {
        method: "POST",
        token,
        body: { characterId, path, disengage },
      }),
  });

  const commit = useMutation({
    mutationFn: ({ characterId, path, disengage }: {
      characterId: string;
      path: PathPoint[];
      disengage: boolean;
    }) =>
      apiFetch<MovementCommitResult>(`/campaigns/${campaignId}/sessions/${sessionId}/movement/commit`, {
        method: "POST",
        token,
        body: { characterId, path, disengage },
      }),
    onSuccess: refreshScene,
  });

  const respond = useMutation({
    mutationFn: ({ reactionId, useReaction }: { reactionId: string; useReaction: boolean }) =>
      apiFetch<MovementCommitResult>(
        `/campaigns/${campaignId}/sessions/${sessionId}/movement/reactions/${reactionId}/respond`,
        { method: "POST", token, body: { useReaction } },
      ),
    onSuccess: refreshScene,
  });

  return { preview, commit, respond };
}

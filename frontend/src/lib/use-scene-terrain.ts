"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "./api";
import { useAuthStore } from "./auth-store";
import { chunkCells, MAX_CELLS_PER_PAINT, type Cell, type TerrainPaintTool } from "./terrain-paint";

/**
 * Sprint H Part 6 — DM ระบายพื้นเดินยาก/กำแพง (`PATCH .../terrain` ซึ่งเป็น DM-only ที่ชั้น API)
 *
 * แยก hook ของตัวเองแบบเดียวกับ `use-scene-tokens` — หน้าที่ไม่ใช่ DM (ผู้เล่น/จอกลาง) จะไม่ลาก mutation
 * นี้ติดไปด้วย · เส้นที่ลากยาวเกินที่ server รับต่อครั้งจะถูกแบ่งส่งทีละก้อน
 */
export function useSceneTerrain(campaignId: string, sceneId: string | null) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const paint = useMutation({
    mutationFn: async ({ tool, cells }: { tool: TerrainPaintTool; cells: Cell[] }) => {
      for (const chunk of chunkCells(cells, MAX_CELLS_PER_PAINT)) {
        await apiFetch<unknown>(`/campaigns/${campaignId}/scenes/${sceneId}/terrain`, {
          method: "PATCH",
          token,
          body: { paint: tool, squares: chunk },
        });
      }
    },
    // onSettled ไม่ใช่ onSuccess — ถ้าก้อนหลัง ๆ พลาด ก้อนแรกบันทึกไปแล้ว ต้องดึงของจริงมาแสดง
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ["scene"] });
      // พื้นเปลี่ยน = ขอบเขตที่เดินได้เปลี่ยน (เหตุผลเดียวกับ use-movement)
      void queryClient.invalidateQueries({ queryKey: ["reachable"] });
    },
  });

  return { paint };
}

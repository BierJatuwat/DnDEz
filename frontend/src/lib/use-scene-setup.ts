"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { API_BASE_URL, ApiError, apiFetch } from "./api";
import { useAuthStore } from "./auth-store";

/**
 * Sprint H Part 3 — ตั้งฉาก: อัปโหลดแผนที่ → สร้างฉาก → ตั้งกริด
 *
 * endpoint ทั้งสามมีมาตั้งแต่ Sprint G Part 4 แต่ **ไม่เคยมี UI เรียกเลย** — พบตอนเดิน onboarding
 * ด้วยมือจริงว่า DM เห็นข้อความ "อัปโหลดแผนที่แล้วสร้างฉากก่อน" แต่ไม่มีปุ่มให้กดสักปุ่ม
 * (ตรวจแล้ว: `input[type=file]` บนหน้า DM = 0 ตัว)
 *
 * ⚠️ การอัปโหลดเป็น **multipart** ใช้ `apiFetch` ไม่ได้ (ตัวนั้น JSON อย่างเดียวและตั้ง
 * Content-Type เอง ซึ่งจะทำให้ boundary ของ multipart หาย) จึงเรียก `fetch` ตรงที่นี่
 * และ **ห้ามตั้ง Content-Type เอง** — ต้องปล่อยให้เบราว์เซอร์ใส่ boundary ให้
 */
export function useSceneSetup(campaignId: string, sessionId: string | null) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const refreshScene = () => queryClient.invalidateQueries({ queryKey: ["scene"] });

  /** อัปโหลดรูป → ได้ mapAssetId → สร้างฉากที่ผูกกับรูปนั้น (ทำต่อกันเป็นก้าวเดียวสำหรับผู้ใช้) */
  const createSceneWithMap = useMutation({
    mutationFn: async ({ file, name }: { file: File; name: string }) => {
      const form = new FormData();
      form.append("file", file);

      const uploadResponse = await fetch(`${API_BASE_URL}/campaigns/${campaignId}/map-assets`, {
        method: "POST",
        headers: token ? { Authorization: `Bearer ${token}` } : {},
        body: form,
      });

      const uploadText = await uploadResponse.text();
      const uploadBody = uploadText ? JSON.parse(uploadText) : null;
      if (!uploadResponse.ok) {
        throw new ApiError(uploadResponse.status, uploadBody);
      }

      return apiFetch<{ id: string }>(`/campaigns/${campaignId}/sessions/${sessionId}/scene`, {
        method: "POST",
        token,
        body: { name, mapAssetId: (uploadBody as { id: string }).id },
      });
    },
    onSuccess: refreshScene,
  });

  const setGrid = useMutation({
    mutationFn: ({ sceneId, pixelsPerSquare, offsetX, offsetY }: {
      sceneId: string;
      pixelsPerSquare: number;
      offsetX: number;
      offsetY: number;
    }) =>
      apiFetch<unknown>(`/campaigns/${campaignId}/scenes/${sceneId}/grid`, {
        method: "PATCH",
        token,
        body: { pixelsPerSquare, offsetX, offsetY },
      }),
    onSuccess: refreshScene,
  });

  return { createSceneWithMap, setGrid };
}

/** แปลงเหตุผลที่ server ส่งกลับให้เป็นข้อความที่ DM อ่านแล้วรู้ว่าต้องทำอะไรต่อ */
export function uploadErrorMessage(error: unknown): string {
  const reason =
    error instanceof ApiError && typeof error.body === "object" && error.body !== null
      ? (error.body as { reason?: string }).reason
      : undefined;

  switch (reason) {
    case "file_required":
      return "ยังไม่ได้เลือกไฟล์";
    case "file_too_large":
      return "ไฟล์ใหญ่เกินไป — ลองย่อรูปก่อน";
    case "invalid_file_type":
      return "ไฟล์นี้ไม่ใช่รูปภาพ (รองรับ PNG/JPEG/WebP/GIF)";
    case "quota_exceeded":
      return "พื้นที่เก็บไฟล์ของคุณเต็มแล้ว — ลบแผนที่เก่าออกก่อน";
    case "map_asset_not_found":
      return "หาแผนที่ที่อัปโหลดไม่เจอ ลองอัปโหลดใหม่อีกครั้ง";
    default:
      return "อัปโหลดไม่สำเร็จ ลองใหม่อีกครั้ง";
  }
}

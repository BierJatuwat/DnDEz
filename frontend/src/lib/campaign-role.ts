"use client";

import { useQuery } from "@tanstack/react-query";
import { apiFetch, ApiError } from "./api";
import { useAuthStore } from "./auth-store";
import type { CampaignMember } from "./campaign-types";

export type CampaignRole = "Dm" | "Player";

/**
 * Sprint F งานที่ 6a — บทบาทของผู้ใช้ในแคมเปญนี้ **มาจาก server เท่านั้น**
 *
 * เดิม (Sprint E Part 3 – F งานที่ 2b) หน้าเดียวเดา role เองฝั่ง client ด้วย
 * `campaign.ownerUserId === userId` ซึ่งใช้ได้เพราะมีหน้าเดียว แต่พอแยกเป็นสองหน้าจริง การเดาแบบนั้น
 * แปลว่า client เป็นคนตัดสินว่าตัวเองเป็น DM ไหม — ที่นี่อ่านจาก `GET /campaigns/{id}/members`
 * (ค่าที่ server ตอบมาจาก `campaign_members.role` จริง) แทน
 *
 * ⚠️ **role ที่ได้จากที่นี่ใช้ตัดสินแค่ "จะแสดง UI แบบไหน" เท่านั้น ไม่ใช่ security boundary** —
 * ทุก endpoint ฝั่ง API เช็คสิทธิ์เองอิสระอยู่แล้ว (`CampaignAuthorization.RequireCampaignDmAsync`
 * ซึ่งเทียบ `campaigns.owner_user_id`) ต่อให้ client ปลอม role ที่นี่ได้ ก็ได้แค่เห็นปุ่มที่กดแล้ว
 * ได้ 403 กลับมา ไม่ได้สิทธิ์เพิ่มจริง
 *
 * หมายเหตุเรื่อง "นิยาม DM สองชั้น": ชั้น API ใช้ `owner_user_id` ส่วนชั้นนี้ใช้ `role` — ทั้งสองตรงกัน
 * ได้เพราะ invariant ที่ `app_create_campaign()` insert เจ้าของเป็นสมาชิก role `'Dm'` เสมอ
 * (ดู doc comment ของ `CampaignAuthorization.cs`) ถ้า invariant นั้นพังวันไหน อาการที่เห็นคือ UI โชว์
 * ผิดหน้า ไม่ใช่สิทธิ์รั่ว
 */
export function useCampaignRole(campaignId: string | null) {
  const token = useAuthStore((s) => s.token);
  const userId = useAuthStore((s) => s.userId);

  const membersQuery = useQuery({
    queryKey: ["campaign-members", campaignId],
    queryFn: () =>
      apiFetch<CampaignMember[]>(`/campaigns/${campaignId}/members`, { token }),
    enabled: !!token && !!campaignId,
    // 404 = ไม่ใช่สมาชิกแคมเปญนี้ (RLS กรองออก) — retry ไม่ช่วยอะไร แค่หน่วงหน้าจอเปล่า ๆ
    retry: false,
  });

  const me = membersQuery.data?.find((m) => m.userId === userId);
  const role: CampaignRole | null =
    me === undefined ? null : me.role === "Dm" ? "Dm" : "Player";

  const notMember =
    membersQuery.isError &&
    membersQuery.error instanceof ApiError &&
    membersQuery.error.status === 404;

  return {
    role,
    isDm: role === "Dm",
    members: membersQuery.data ?? [],
    isLoading: membersQuery.isPending,
    /** ไม่ใช่สมาชิกแคมเปญนี้ (หรือแคมเปญไม่มีอยู่จริง — API ตอบ 404 เหมือนกันโดยตั้งใจ กัน enumeration) */
    notMember,
    isError: membersQuery.isError,
  };
}

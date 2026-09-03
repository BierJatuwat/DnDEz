"use client";

import { useEffect } from "react";
import { useParams, useRouter } from "next/navigation";
import { useCampaignRole } from "@/lib/campaign-role";
import { DmConsole } from "@/components/DmConsole";

/**
 * Sprint F งานที่ 6a — หน้า DM · บทบาทมาจาก server (`useCampaignRole`) ไม่ใช่เดาจาก `ownerUserId`
 *
 * ผู้เล่นที่เปิด URL นี้ตรง ๆ จะถูกส่งไป `/play` แทนที่จะปล่อยให้เห็นหน้า DM ที่ทุกปุ่มตอบ 403
 * (endpoint ฝั่ง API กันเองอยู่แล้ว — การ redirect ตรงนี้เป็นเรื่อง UX ไม่ใช่ตัวกันสิทธิ์)
 */
export default function DmPage() {
  const router = useRouter();
  const params = useParams<{ campaignId: string }>();
  const campaignId = params.campaignId;
  const { isDm, role, isLoading, notMember, isError } = useCampaignRole(campaignId);

  useEffect(() => {
    if (role && role !== "Dm") {
      router.replace(`/campaigns/${campaignId}/play`);
    }
  }, [role, campaignId, router]);

  if (notMember) {
    return <p className="text-sm text-red-500">ไม่พบโต๊ะนี้ หรือคุณไม่ได้เป็นสมาชิก</p>;
  }

  if (isError) {
    return <p className="text-sm text-red-500">โหลดข้อมูลบทบาทไม่สำเร็จ</p>;
  }

  if (isLoading || !isDm) {
    return <p className="opacity-50 text-sm">กำลังโหลด...</p>;
  }

  return <DmConsole campaignId={campaignId} />;
}

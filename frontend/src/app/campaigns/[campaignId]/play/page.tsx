"use client";

import { useParams } from "next/navigation";
import { useCampaignRole } from "@/lib/campaign-role";
import { PlayerView } from "@/components/PlayerView";

/**
 * Sprint F งานที่ 6a — หน้าผู้เล่น · เปิดให้ **สมาชิกทุกบทบาท** เข้าได้ รวมถึง DM
 *
 * ตั้งใจไม่ redirect DM ออกจากหน้านี้ (ต่างจากหน้า `/dm` ที่เด้งผู้เล่นออก) — DM เปิดดูว่าผู้เล่น
 * เห็นอะไรได้เป็นเรื่องมีประโยชน์จริง โดยเฉพาะตอนตั้งค่าซ่อน stat ของ NPC (งานที่ 7) ที่ต้องรู้ว่า
 * ฝั่งผู้เล่นเห็นอะไรบ้าง · ทิศทางที่ต้องกันคือผู้เล่นเปิดหน้า DM แล้วเจอปุ่มที่กดแล้ว 403 ทุกอัน
 */
export default function PlayPage() {
  const params = useParams<{ campaignId: string }>();
  const campaignId = params.campaignId;
  const { role, isLoading, notMember, isError } = useCampaignRole(campaignId);

  if (notMember) {
    return <p className="text-sm text-red-500">ไม่พบโต๊ะนี้ หรือคุณไม่ได้เป็นสมาชิก</p>;
  }

  if (isError) {
    return <p className="text-sm text-red-500">โหลดข้อมูลบทบาทไม่สำเร็จ</p>;
  }

  if (isLoading || !role) {
    return <p className="opacity-50 text-sm">กำลังโหลด...</p>;
  }

  return <PlayerView campaignId={campaignId} />;
}

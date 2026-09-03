"use client";

import { useEffect } from "react";
import { useParams, useRouter } from "next/navigation";
import { useCampaignRole } from "@/lib/campaign-role";

/**
 * Sprint F งานที่ 6a — ทางเข้ากลางของแคมเปญ: ถาม server ว่าเราเป็นบทบาทอะไรในโต๊ะนี้ แล้วส่งต่อไป
 * หน้าที่ถูกต้อง (`/dm` หรือ `/play`)
 *
 * ทำไมต้องมีหน้ากลาง แทนที่จะให้หน้าแรกลิงก์ตรงไป `/dm`/`/play` เลย: ถ้าลิงก์ตรง หน้าแรกต้องรู้
 * บทบาทของทุกแคมเปญในลิสต์ ซึ่งข้อมูลที่มีตรงนั้นคือ `ownerUserId` — กลายเป็นการเดาบทบาทฝั่ง client
 * อีกแบบหนึ่ง (สิ่งที่งานนี้ตั้งใจเลิกทำ) การมีหน้ากลางทำให้ตัดสินจากข้อมูล server ที่เดียวจบ
 */
export default function CampaignEntryPage() {
  const router = useRouter();
  const params = useParams<{ campaignId: string }>();
  const campaignId = params.campaignId;
  const { role, isLoading, notMember, isError } = useCampaignRole(campaignId);

  useEffect(() => {
    if (!role) return;
    router.replace(`/campaigns/${campaignId}/${role === "Dm" ? "dm" : "play"}`);
  }, [role, campaignId, router]);

  if (notMember) {
    return (
      <p className="text-sm text-red-500">
        ไม่พบโต๊ะนี้ หรือคุณไม่ได้เป็นสมาชิก — ขอโค้ดเข้าร่วมจาก DM แล้วกรอกที่หน้าแรก
      </p>
    );
  }

  if (isError) {
    return <p className="text-sm text-red-500">โหลดข้อมูลโต๊ะไม่สำเร็จ</p>;
  }

  return (
    <p className="opacity-50 text-sm">{isLoading ? "กำลังตรวจสอบบทบาท..." : "กำลังเข้าโต๊ะ..."}</p>
  );
}

"use client";

import { useParams } from "next/navigation";
import { useCampaignRole } from "@/lib/campaign-role";
import { TableView } from "@/components/TableView";

/**
 * Sprint G Part 6 — จอกลาง (`/campaigns/{id}/table`) — จอที่สามของโต๊ะ
 *
 * เปิดให้สมาชิกทุกบทบาทเข้าได้เหมือนหน้า `/play` (ไม่เด้งใครออก) เพราะในทางปฏิบัติคนที่เอาแท็บเล็ต/
 * ทีวีมาต่อจะล็อกอินด้วยบัญชีของใครก็ได้ที่อยู่ในโต๊ะ · payload ที่เห็นถูกกรองตาม role ของบัญชีนั้น
 * จาก server อยู่แล้ว (design doc §13.3)
 *
 * ⚠️ credential แยกของจอกลาง (§16 — บัญชีอ่านอย่างเดียว เพิกถอนได้ ไม่ผูกกับบัญชีคน) **ยังไม่ได้ทำ**
 * ตามที่ระบุไว้ว่าเลื่อนได้ถ้าเวลาไม่พอ — จนกว่าจะทำ ควรเปิดจอกลางด้วยบัญชี**ผู้เล่น** ไม่ใช่บัญชี DM
 * (บัญชี DM จะเห็น token ที่อยู่ในหมอกด้วย ซึ่งไม่ใช่สิ่งที่ควรฉายให้ทั้งโต๊ะเห็น)
 */
export default function TablePage() {
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

  return <TableView campaignId={campaignId} />;
}

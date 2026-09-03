"use client";

import { useEffect } from "react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";
import { useAuthStore } from "@/lib/auth-store";

/**
 * Sprint F งานที่ 6a — chrome ร่วมของทุกหน้าใต้ `/campaigns/{id}` (หัวเรื่อง + ลิงก์กลับ + logout)
 * และเป็นด่าน auth ด่านเดียวของกลุ่ม route นี้ ไม่ต้องเช็คซ้ำในทุกหน้า
 *
 * ส่วน **บทบาท** (DM/ผู้เล่น) ไม่เช็คที่นี่ — แต่ละหน้าเรียก `useCampaignRole` เอง เพราะ TanStack
 * Query cache ให้อยู่แล้ว (query key เดียวกัน) จึงไม่มี fetch ซ้ำ และไม่ต้องลาก context ผ่าน layout
 */
export default function CampaignLayout({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const params = useParams<{ campaignId: string }>();
  const campaignId = params.campaignId;

  const token = useAuthStore((s) => s.token);
  const ready = useAuthStore((s) => s.hasHydrated);
  const logout = useAuthStore((s) => s.logout);

  useEffect(() => {
    if (ready && !token) {
      router.replace("/login");
    }
  }, [ready, token, router]);

  const campaignQuery = useQuery({
    queryKey: ["campaign", campaignId],
    queryFn: () => apiFetch<{ id: string; name: string }>(`/campaigns/${campaignId}`, { token }),
    enabled: !!token && !!campaignId,
    retry: false,
  });

  if (!ready || !token) {
    return (
      <main className="flex flex-1 items-center justify-center">
        <p className="opacity-50 text-sm">กำลังโหลด...</p>
      </main>
    );
  }

  return (
    <main className="flex-1 p-6 space-y-6 max-w-3xl mx-auto w-full">
      <div className="flex items-center justify-between">
        <div>
          <Link href="/" className="text-xs opacity-60 hover:opacity-100 underline">
            ← ทุกโต๊ะ
          </Link>
          <h1 className="text-xl font-semibold">
            {campaignQuery.data?.name ?? "กำลังโหลด..."}
          </h1>
        </div>
        <button onClick={logout} className="text-sm underline opacity-70 hover:opacity-100">
          ออกจากระบบ
        </button>
      </div>
      {children}
    </main>
  );
}

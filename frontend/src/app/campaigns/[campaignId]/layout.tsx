"use client";

import { useEffect } from "react";
import Link from "next/link";
import { useParams, usePathname, useRouter } from "next/navigation";
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
  const pathname = usePathname();

  /**
   * Sprint G Part 6 — จอกลาง (`/table`) ใช้ด่าน auth ร่วมกับหน้าอื่น แต่**ไม่เอา chrome**
   *
   * ไม่ใช่เรื่องความสวยงาม: ตาราง projection §13.1 ระบุว่าช่อง Input ของจอกลางคือ "ไม่มีเลย"
   * แต่ chrome ชุดนี้มีปุ่ม **ออกจากระบบ** กับลิงก์ย้อนกลับติดมาด้วย — บนทีวี/แท็บเล็ตที่วางกลางโต๊ะ
   * ให้ทุกคนเอื้อมถึง นั่นคือปุ่มที่ใครเผลอชนแล้วจอกลางหลุดออกจากระบบกลางเกม · เจอตอนเปิดดูจริง
   * ในเบราว์เซอร์ (แถบ sticky ลอยทับแผนที่) ไม่ใช่ตอนอ่านโค้ด
   */
  const isTableView = pathname?.endsWith("/table") ?? false;

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

  if (isTableView) {
    return <main className="flex-1 w-full">{children}</main>;
  }

  return (
    <main className="flex-1 w-full">
      {/* หัวเรื่อง sticky — หน้านี้ยาวพอที่การเลื่อนกลับขึ้นไปหาปุ่ม "ทุกโต๊ะ" จะน่ารำคาญ
          (defect A4: "scroll ลงเยอะไป") */}
      <div className="sticky top-0 z-10 border-b border-black/10 dark:border-white/10 bg-white/85 dark:bg-black/85 backdrop-blur">
        <div className="max-w-[1600px] mx-auto w-full px-6 py-3 flex items-center justify-between gap-4">
          <div className="min-w-0">
            <Link href="/" className="text-xs opacity-60 hover:opacity-100 underline">
              ← ทุกโต๊ะ
            </Link>
            <h1 className="text-xl font-semibold truncate">
              {campaignQuery.data?.name ?? "กำลังโหลด..."}
            </h1>
          </div>
          <button
            onClick={logout}
            className="text-sm underline opacity-70 hover:opacity-100 shrink-0"
          >
            ออกจากระบบ
          </button>
        </div>
      </div>
      <div className="max-w-[1600px] mx-auto w-full p-6 space-y-4">{children}</div>
    </main>
  );
}

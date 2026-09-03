"use client";

import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch, ApiError } from "@/lib/api";
import { useAuthStore } from "@/lib/auth-store";
import type { Campaign } from "@/lib/campaign-types";
import { useState } from "react";

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";

/**
 * Sprint F งานที่ 6a — หน้าแรกหลัง login กลายเป็น "ตัวเลือกโต๊ะ" แทนที่จะเป็น console ทั้งก้อน
 * (เดิม `CampaignConsole` ยัดทุกอย่างไว้หน้าเดียว)
 *
 * ที่สำคัญที่สุดของหน้านี้คือ **ช่องกรอก join code** — ก่อนงานนี้ผู้เล่นเข้าแคมเปญไม่ได้เลยถ้าไม่
 * INSERT `campaign_members` ผ่าน SQL เอง ซึ่งขัดกับเป้าหมาย Sprint F ("เล่นจบ 1 session ได้ ไม่ต้อง
 * แตะ DB/curl เอง") ตรง ๆ · endpoint `POST /campaigns/join` มีมาตั้งแต่งานที่ 1 แต่ไม่เคยมี UI
 *
 * ลิงก์ของแต่ละแคมเปญชี้ไป `/campaigns/{id}` เฉย ๆ (ไม่ใช่ `/dm` หรือ `/play` ตรง ๆ) เพราะหน้านั้น
 * เป็นตัวถาม server ว่าเราเป็นบทบาทอะไรแล้วค่อย redirect — ไม่ให้ client เดาเองจาก `ownerUserId`
 */
export function CampaignPicker() {
  const router = useRouter();
  const token = useAuthStore((s) => s.token);
  const userId = useAuthStore((s) => s.userId);
  const logout = useAuthStore((s) => s.logout);
  const queryClient = useQueryClient();
  const [joinError, setJoinError] = useState<string | null>(null);

  const campaignsQuery = useQuery({
    queryKey: ["campaigns"],
    queryFn: () => apiFetch<Campaign[]>("/campaigns", { token }),
    enabled: !!token,
  });

  const createCampaign = useMutation({
    mutationFn: (name: string) =>
      apiFetch<{ id: string }>("/campaigns", { method: "POST", token, body: { name } }),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: ["campaigns"] });
      router.push(`/campaigns/${created.id}`);
    },
  });

  const joinCampaign = useMutation({
    mutationFn: (joinCode: string) =>
      apiFetch<{ campaignId: string }>("/campaigns/join", {
        method: "POST",
        token,
        body: { joinCode },
      }),
    onSuccess: async (result) => {
      setJoinError(null);
      await queryClient.invalidateQueries({ queryKey: ["campaigns"] });
      router.push(`/campaigns/${result.campaignId}`);
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        // 404 = ไม่พบโค้ด (API ตอบเหมือนกันทั้ง "ไม่มีโค้ดนี้" และ "มีแต่มองไม่เห็น" กัน enumeration)
        // 429 = ยิงถี่เกิน (rate limit 10/นาที ต่อ user — เป็น enumeration surface ตรง ๆ)
        if (err.status === 404) setJoinError("ไม่พบโค้ดนี้ — ตรวจสอบกับ DM อีกครั้ง");
        else if (err.status === 429) setJoinError("ลองบ่อยเกินไป รอสักครู่แล้วลองใหม่");
        else setJoinError(`เข้าร่วมไม่สำเร็จ: HTTP ${err.status}`);
      } else {
        setJoinError("เข้าร่วมไม่สำเร็จ");
      }
    },
  });

  const campaigns = campaignsQuery.data ?? [];

  return (
    <main className="flex-1 p-6 space-y-6 max-w-3xl mx-auto w-full">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">DnDEz</h1>
          <p className="text-sm opacity-70">user: {userId}</p>
        </div>
        <button onClick={logout} className="text-sm underline opacity-70 hover:opacity-100">
          ออกจากระบบ
        </button>
      </div>

      {campaignsQuery.isError && (
        <p className="text-sm text-red-500">โหลดรายชื่อแคมเปญไม่สำเร็จ</p>
      )}

      <section className={sectionClass}>
        <h2 className="font-medium">โต๊ะของฉัน</h2>
        <ul className="space-y-1">
          {campaigns.map((c) => (
            <li key={c.id}>
              <button
                onClick={() => router.push(`/campaigns/${c.id}`)}
                className="w-full text-left rounded px-3 py-2 hover:bg-black/5 dark:hover:bg-white/5"
              >
                {c.name} <span className="opacity-50 text-xs">({c.mode})</span>
              </button>
            </li>
          ))}
          {campaigns.length === 0 && (
            <li className="opacity-50 text-sm">ยังไม่ได้อยู่โต๊ะไหนเลย — สร้างใหม่ หรือเข้าร่วมด้วยโค้ด</li>
          )}
        </ul>
      </section>

      <section className={sectionClass}>
        <h2 className="font-medium">เข้าร่วมโต๊ะด้วยโค้ด</h2>
        <p className="text-xs opacity-60">ขอโค้ดจาก DM ของโต๊ะนั้น</p>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            const input = e.currentTarget.elements.namedItem("joinCode") as HTMLInputElement;
            if (input.value.trim()) {
              joinCampaign.mutate(input.value.trim());
              input.value = "";
            }
          }}
          className="flex gap-2"
        >
          <input
            name="joinCode"
            placeholder="โค้ดเข้าร่วม"
            className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
          />
          <button
            type="submit"
            disabled={joinCampaign.isPending}
            className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
          >
            เข้าร่วม
          </button>
        </form>
        {joinError && <p className="text-sm text-red-500">{joinError}</p>}
      </section>

      <section className={sectionClass}>
        <h2 className="font-medium">สร้างโต๊ะใหม่ (คุณจะเป็น DM)</h2>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            const input = e.currentTarget.elements.namedItem(
              "campaignName",
            ) as HTMLInputElement;
            if (input.value.trim()) {
              createCampaign.mutate(input.value.trim());
              input.value = "";
            }
          }}
          className="flex gap-2"
        >
          <input
            name="campaignName"
            placeholder="ชื่อแคมเปญใหม่"
            className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
          />
          <button
            type="submit"
            disabled={createCampaign.isPending}
            className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
          >
            สร้าง
          </button>
        </form>
      </section>
    </main>
  );
}

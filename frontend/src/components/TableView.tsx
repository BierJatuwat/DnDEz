"use client";

import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";
import { useAuthStore } from "@/lib/auth-store";
import { useScene } from "@/lib/use-scene";
import { partyHealth } from "@/lib/scene-view";
import { roundOf, type Encounter } from "@/lib/combat-types";
import type { CharacterSummary, GameSession } from "@/lib/campaign-types";
import { SceneMap } from "./SceneMap";
import { parseSceneGrid } from "@/lib/scene-grid";

/**
 * Sprint G Part 6 — **จอกลาง (Table view)** — มุมมองที่สามที่มีบุคลิกของตัวเอง (design doc §13)
 *
 * > "จอกลางไม่ใช่ 'จอผู้เล่นที่ขยายใหญ่' แต่เป็น view type ที่สามที่มีบุคลิกของตัวเอง —
 * >  ตัวอักษรใหญ่ ไม่มี input เลย และแสดง HP รวมทั้งปาร์ตี้"
 *
 * ⚠️ **ทำไมไฟล์นี้ไม่เรียก `useCampaignWorkspace` / `useCombatActions` ที่มีอยู่แล้ว** ทั้งที่จะได้
 * ข้อมูลครบกว่าและเขียนสั้นกว่า: hook สองตัวนั้นสร้าง **mutation** (ส่ง action, ทอยเต๋า, เดินเทิร์น,
 * อนุมัติ proposal ฯลฯ) ติดมาด้วยเสมอ · ช่อง Input ของจอกลางในตาราง §13.1 คือ **"ไม่มีเลย"** —
 * การไม่เรนเดอร์ปุ่มแต่ยังถือ mutation ไว้ในมือ แปลว่า "ไม่มี input" เป็นแค่เรื่องที่เผลอลืมใส่ปุ่ม
 * ได้ตลอดเวลา · ที่นี่จึงประกาศ query แบบอ่านอย่างเดียวเองทั้งหมด **จอนี้ยิงอะไรกลับไปไม่ได้เลย
 * เพราะไม่มีเครื่องมือจะยิง** ไม่ใช่เพราะเราจำได้ว่าอย่าใส่ปุ่ม
 *
 * ⚠️ ความลับของ DM ไม่ต้องกรองที่นี่ — จอกลางล็อกอินด้วยบัญชีอะไรก็ได้ที่เป็นสมาชิกโต๊ะ และ server
 * กรอง payload ตาม role ของ token นั้นให้แล้ว (§13.3) การกรองซ้ำฝั่งนี้จะทำให้เข้าใจผิดว่าจอนี้เป็น
 * ด่านกันข้อมูล ทั้งที่ด่านจริงอยู่ฝั่ง server · **ข้อจำกัดที่ต้องรู้**: ถ้าเอาบัญชี DM มาเปิดจอกลาง
 * จะเห็นสิ่งที่ DM เห็น (รวม token ในหมอก) เพราะ server ตอบตาม role ของบัญชีที่ยิงมา —
 * credential แยกสำหรับจอกลาง (§16) ยังไม่ได้ทำ เป็นของค้างที่บันทึกไว้แล้ว
 */
export function TableView({ campaignId }: { campaignId: string }) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();

  const sessionsQuery = useQuery({
    queryKey: ["sessions", campaignId],
    queryFn: () => apiFetch<GameSession[]>(`/campaigns/${campaignId}/sessions`, { token }),
    enabled: !!token,
  });

  // จอกลางไม่มีตัวเลือกเซสชัน (ไม่มี input) — ยึดเซสชันที่กำลังเล่นอยู่โดยอัตโนมัติ
  const activeSession =
    sessionsQuery.data?.find((s) => s.status === "Active") ?? sessionsQuery.data?.[0] ?? null;
  const sessionId = activeSession?.id ?? null;

  const charactersQuery = useQuery({
    queryKey: ["characters", campaignId],
    queryFn: () => apiFetch<CharacterSummary[]>(`/campaigns/${campaignId}/characters`, { token }),
    enabled: !!token,
  });

  const encounterQuery = useQuery({
    queryKey: ["encounter-active", sessionId],
    queryFn: () =>
      apiFetch<Encounter | null>(
        `/campaigns/${campaignId}/sessions/${sessionId}/encounters/active`,
        { token },
      ),
    enabled: !!token && !!sessionId,
  });

  const { scene, tokens, revealedAreas, noScene } = useScene(campaignId, sessionId);

  // จอกลางเปิดค้างทั้งคืนโดยไม่มีคนแตะ — ไม่มี SignalR ที่นี่โดยตั้งใจ (hub manager ผูกกับ
  // console-store ที่เป็นของหน้าส่วนตัว) ใช้ polling ถี่พอให้ตามเกมทันแทน เรียบง่ายและไม่มีสถานะ
  // ค้างให้พังตอนเน็ตกระตุก
  useEffect(() => {
    const timer = setInterval(() => {
      void queryClient.invalidateQueries({ queryKey: ["scene"] });
      void queryClient.invalidateQueries({ queryKey: ["characters", campaignId] });
      void queryClient.invalidateQueries({ queryKey: ["encounter-active"] });
    }, 5000);
    return () => clearInterval(timer);
  }, [queryClient, campaignId]);

  const characters = charactersQuery.data ?? [];
  const nameOf = (entityId: string) => characters.find((c) => c.id === entityId)?.name;
  const health = partyHealth(characters);
  const encounter = encounterQuery.data ?? null;
  const currentName = encounter?.currentCharacterId
    ? nameOf(encounter.currentCharacterId)
    : undefined;

  return (
    <div className="min-h-screen bg-neutral-950 text-neutral-100 p-8 space-y-6">
      {/* หัวจอ: ชื่อฉาก + พลังชีวิตรวมของปาร์ตี้ — สองอย่างที่ต้องอ่านออกจากอีกฝั่งของโต๊ะ */}
      <header className="flex items-start justify-between gap-6">
        <div>
          <h1 className="text-5xl font-semibold tracking-wide">
            {scene?.name ?? activeSession?.status ?? "ยังไม่เริ่มเล่น"}
          </h1>
          {encounter && (
            <p className="mt-2 text-2xl opacity-70">รอบที่ {roundOf(encounter)}</p>
          )}
        </div>

        <div className="rounded-xl border-2 border-green-500 px-6 py-4 text-right">
          <p className="text-lg uppercase tracking-widest text-green-400">พลังชีวิตปาร์ตี้</p>
          <p className="text-5xl font-bold tabular-nums text-green-400">
            {health.current}
            <span className="text-3xl opacity-60"> / {health.max}</span>
          </p>
        </div>
      </header>

      {/* เทิร์นของใคร — ตัวใหญ่สุดบนจอรองจากชื่อฉาก เพราะเป็นสิ่งที่คนรอบโต๊ะต้องรู้ตลอดเวลา */}
      {currentName && (
        <p className="inline-block rounded-lg bg-green-500 px-6 py-3 text-3xl font-bold text-neutral-950">
          เทิร์นของ: {currentName}
        </p>
      )}

      {noScene ? (
        <p className="rounded border border-white/10 p-10 text-center text-2xl opacity-60">
          ยังไม่มีแผนที่ในเซสชันนี้
        </p>
      ) : (
        <SceneMap
          tokens={tokens}
          revealedAreas={revealedAreas}
          imageUrl={scene?.imageUrl ?? null}
          nameOf={nameOf}
          fogStyle="opaque"
          scale="table"
          emptyHint="ยังไม่มีอะไรบนแผนที่"
          pixelsPerSquare={parseSceneGrid(scene?.grid).pixelsPerSquare}
        />
      )}

      <p className="text-center text-xl opacity-50">
        พื้นที่มืดคือที่ที่ยังไม่มีใครไปถึง
      </p>
    </div>
  );
}

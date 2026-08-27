"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuthStore } from "@/lib/auth-store";
import { useConsoleStore } from "@/lib/console-store";
import { apiFetch, ApiError } from "@/lib/api";
import { useCampaignHub } from "@/lib/use-campaign-hub";

type Campaign = { id: string; name: string; mode: string; createdAt: string };
type GameSession = {
  id: string;
  mode: string;
  status: string;
  joinCode: string | null;
};
type DieRollDetail = {
  sides: number;
  value: number;
  dropped: boolean;
  sign: number;
};
type DiceRolledEvent = {
  id: string;
  sessionId: string;
  characterId: string | null;
  expression: string;
  rolls: DieRollDetail[];
  total: number;
  source: string;
};

/**
 * Sprint E Part 1 — proof ว่า frontend คุยกับ Sprint A-D จริงครบ auth + REST + rules engine +
 * SignalR realtime: สร้างแคมเปญ/เซสชันจริงผ่าน API, join SignalR group จริง, ทอยลูกเต๋าจริงผ่าน
 * DiceEngine แล้วเห็นผล broadcast กลับมาแบบ realtime (ไม่ใช่แค่ response ของ REST call เดียวกัน)
 * — Part 2/3 จะปรับ UX ให้เป็น DM console/player action input จริงต่อจากโครงนี้
 *
 * server state (campaigns/sessions) ผ่าน TanStack Query, UI state ผ่าน Zustand (console-store) —
 * ตาม stack ที่ล็อกไว้ใน PROJECT-CONTEXT §3
 */
export function CampaignConsole() {
  const token = useAuthStore((s) => s.token);
  const userId = useAuthStore((s) => s.userId);
  const logout = useAuthStore((s) => s.logout);
  const queryClient = useQueryClient();

  const selectedCampaignId = useConsoleStore((s) => s.selectedCampaignId);
  const selectedSessionId = useConsoleStore((s) => s.selectedSessionId);
  const joinedCampaignId = useConsoleStore((s) => s.joinedCampaignId);
  const hubStatus = useConsoleStore((s) => s.hubStatus);
  const log = useConsoleStore((s) => s.log);
  const rollExpression = useConsoleStore((s) => s.rollExpression);
  const setSelectedCampaignId = useConsoleStore((s) => s.setSelectedCampaignId);
  const setSelectedSessionId = useConsoleStore((s) => s.setSelectedSessionId);
  const setJoinedCampaignId = useConsoleStore((s) => s.setJoinedCampaignId);
  const setHubStatus = useConsoleStore((s) => s.setHubStatus);
  const setRollExpression = useConsoleStore((s) => s.setRollExpression);
  const appendLog = useConsoleStore((s) => s.appendLog);

  const campaignsQuery = useQuery({
    queryKey: ["campaigns"],
    queryFn: () => apiFetch<Campaign[]>("/campaigns", { token }),
    enabled: !!token,
  });

  const sessionsQuery = useQuery({
    queryKey: ["sessions", selectedCampaignId],
    queryFn: () =>
      apiFetch<GameSession[]>(`/campaigns/${selectedCampaignId}/sessions`, {
        token,
      }),
    enabled: !!token && !!selectedCampaignId,
  });

  const createCampaign = useMutation({
    mutationFn: (name: string) =>
      apiFetch<{ id: string }>("/campaigns", {
        method: "POST",
        token,
        body: { name },
      }),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: ["campaigns"] });
      setSelectedCampaignId(created.id);
    },
  });

  const createSession = useMutation({
    mutationFn: () =>
      apiFetch<{ id: string }>(
        `/campaigns/${selectedCampaignId}/sessions`,
        { method: "POST", token, body: { mode: "Online" } },
      ),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({
        queryKey: ["sessions", selectedCampaignId],
      });
      setSelectedSessionId(created.id);
    },
  });

  const rollDice = useMutation({
    mutationFn: () =>
      apiFetch<{ total: number; expression: string }>(
        `/campaigns/${selectedCampaignId}/sessions/${selectedSessionId}/dice-rolls`,
        { method: "POST", token, body: { expression: rollExpression } },
      ),
    onSuccess: (result) => {
      appendLog(
        `✅ REST response: ${result.expression} = ${result.total} (รอ broadcast ตามมา...)`,
      );
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        appendLog(`ทอยลูกเต๋าไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  // SignalR connection lifecycle + group rejoin-on-reconnect: ดูตรรกะจริงใน
  // src/lib/campaign-hub-manager.ts (เทสต์แยกต่างหากที่ campaign-hub-manager.test.ts)
  useCampaignHub({
    token,
    selectedCampaignId,
    onStatusChange: setHubStatus,
    onJoinedChange: setJoinedCampaignId,
    onLog: appendLog,
    onDiceRolled: (payload) => {
      const event = payload as DiceRolledEvent;
      appendLog(
        `📡 realtime DiceRolled: ${event.expression} = ${event.total} (source=${event.source})`,
      );
    },
  });

  const campaigns = campaignsQuery.data ?? [];
  const sessions = sessionsQuery.data ?? [];

  return (
    <main className="flex-1 p-6 space-y-6 max-w-3xl mx-auto w-full">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">DnDEz — DM console (proof of concept)</h1>
          <p className="text-sm opacity-70">user: {userId}</p>
        </div>
        <button
          onClick={logout}
          className="text-sm underline opacity-70 hover:opacity-100"
        >
          ออกจากระบบ
        </button>
      </div>

      <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
        <h2 className="font-medium">
          SignalR:{" "}
          <span
            className={
              hubStatus === "connected" ? "text-green-500" : "text-yellow-500"
            }
          >
            {hubStatus}
          </span>
          {joinedCampaignId && (
            <span className="opacity-70"> · joined {joinedCampaignId}</span>
          )}
        </h2>
      </section>

      {campaignsQuery.isError && (
        <p className="text-sm text-red-500">โหลดรายชื่อแคมเปญไม่สำเร็จ</p>
      )}

      <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
        <h2 className="font-medium">แคมเปญ</h2>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            const form = e.currentTarget;
            const input = form.elements.namedItem(
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
        <ul className="space-y-1">
          {campaigns.map((c) => (
            <li key={c.id}>
              <button
                onClick={() => setSelectedCampaignId(c.id)}
                className={`w-full text-left rounded px-3 py-1.5 ${
                  selectedCampaignId === c.id
                    ? "bg-indigo-600/20"
                    : "hover:bg-black/5 dark:hover:bg-white/5"
                }`}
              >
                {c.name} <span className="opacity-50 text-xs">({c.mode})</span>
              </button>
            </li>
          ))}
          {campaigns.length === 0 && (
            <li className="opacity-50 text-sm">ยังไม่มีแคมเปญ</li>
          )}
        </ul>
      </section>

      {selectedCampaignId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">เซสชัน</h2>
          <button
            onClick={() => createSession.mutate()}
            disabled={createSession.isPending}
            className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
          >
            สร้างเซสชันใหม่ (Online)
          </button>
          <ul className="space-y-1">
            {sessions.map((s) => (
              <li key={s.id}>
                <button
                  onClick={() => setSelectedSessionId(s.id)}
                  className={`w-full text-left rounded px-3 py-1.5 ${
                    selectedSessionId === s.id
                      ? "bg-indigo-600/20"
                      : "hover:bg-black/5 dark:hover:bg-white/5"
                  }`}
                >
                  {s.id.slice(0, 8)} — {s.mode} / {s.status}
                </button>
              </li>
            ))}
            {sessions.length === 0 && (
              <li className="opacity-50 text-sm">ยังไม่มีเซสชัน</li>
            )}
          </ul>
        </section>
      )}

      {selectedSessionId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">
            ทอยลูกเต๋า (proof: REST → rules engine → SignalR broadcast)
          </h2>
          <div className="flex gap-2">
            <input
              value={rollExpression}
              onChange={(e) => setRollExpression(e.target.value)}
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <button
              onClick={() => rollDice.mutate()}
              disabled={rollDice.isPending}
              className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
            >
              ทอย
            </button>
          </div>
        </section>
      )}

      <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-1">
        <h2 className="font-medium mb-2">Log</h2>
        <ul className="space-y-1 text-sm font-mono max-h-64 overflow-y-auto">
          {log.map((entry, i) => (
            <li key={i} className="opacity-80">
              <span className="opacity-50">{entry.at}</span> {entry.text}
            </li>
          ))}
        </ul>
      </section>
    </main>
  );
}

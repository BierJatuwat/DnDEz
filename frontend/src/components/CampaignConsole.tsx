"use client";

import Link from "next/link";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuthStore } from "@/lib/auth-store";
import { useConsoleStore } from "@/lib/console-store";
import { apiFetch, ApiError } from "@/lib/api";
import { useCampaignHub } from "@/lib/use-campaign-hub";
import {
  normalizeProposalCreated,
  mergeProposal,
  removeProposal,
  type Proposal,
} from "@/lib/proposal-queue";

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
// รายการตัวละครจาก GET เบากว่า response ตอน POST (ไม่มี HP/AC) — แยก type ตามจริง ไม่ปนกัน
type CharacterSummary = {
  id: string;
  name: string;
  level: number;
  ownerUserId: string;
  controlledByDm: boolean;
};
type ToolCallResult = { name: string; input: unknown; result: unknown };
type ConsultResponse = {
  messageId: string | null;
  content: string;
  toolCall: ToolCallResult | null;
  pending: { proposalId: string; toolName: string; toolInput: unknown } | null;
};
type ResolveProposalResponse = {
  proposalId: string;
  status: string;
  messageId: string | null;
  content: string;
  toolCall: ToolCallResult | null;
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
  const consultInput = useConsoleStore((s) => s.consultInput);
  const consultMessages = useConsoleStore((s) => s.consultMessages);
  const setSelectedCampaignId = useConsoleStore((s) => s.setSelectedCampaignId);
  const setSelectedSessionId = useConsoleStore((s) => s.setSelectedSessionId);
  const setJoinedCampaignId = useConsoleStore((s) => s.setJoinedCampaignId);
  const setHubStatus = useConsoleStore((s) => s.setHubStatus);
  const setRollExpression = useConsoleStore((s) => s.setRollExpression);
  const setConsultInput = useConsoleStore((s) => s.setConsultInput);
  const appendConsultMessage = useConsoleStore((s) => s.appendConsultMessage);
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

  const charactersQuery = useQuery({
    queryKey: ["characters", selectedCampaignId],
    queryFn: () =>
      apiFetch<CharacterSummary[]>(
        `/campaigns/${selectedCampaignId}/characters`,
        { token },
      ),
    enabled: !!token && !!selectedCampaignId,
  });

  const proposalsQuery = useQuery({
    queryKey: ["proposals", selectedSessionId],
    queryFn: () =>
      apiFetch<Proposal[]>(
        `/campaigns/${selectedCampaignId}/sessions/${selectedSessionId}/proposals`,
        { token },
      ),
    enabled: !!token && !!selectedCampaignId && !!selectedSessionId,
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

  const createCharacter = useMutation({
    // MVP: ฟอร์มสร้างตัวละครรับแค่ชื่อ ฟิลด์อื่น (HP/AC/ระดับ ฯลฯ) ปล่อยให้ฝั่ง server ใส่ default
    // ให้ ยังไม่ทำ character-sheet editor เต็มรูปแบบในสปรินต์นี้
    mutationFn: (name: string) =>
      apiFetch<{ id: string }>(`/campaigns/${selectedCampaignId}/characters`, {
        method: "POST",
        token,
        body: { name },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: ["characters", selectedCampaignId],
      });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        appendLog(`สร้างตัวละครไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  const consult = useMutation({
    mutationFn: (content: string) =>
      apiFetch<ConsultResponse>(
        `/campaigns/${selectedCampaignId}/sessions/${selectedSessionId}/consult`,
        { method: "POST", token, body: { content } },
      ),
    onSuccess: (result) => {
      // response มีได้สามแบบไม่ทับกัน: narration ล้วน / tool รันอัตโนมัติแล้ว / รอ proposal อนุมัติ
      if (result.content) {
        appendConsultMessage("ai", result.content);
      }
      if (result.pending) {
        appendConsultMessage(
          "system",
          `สร้างคำขอ "${result.pending.toolName}" รอการอนุมัติแล้ว — ดูรายละเอียดที่คิว proposal ด้านล่าง`,
        );
      }
      if (result.toolCall) {
        appendConsultMessage(
          "system",
          `รันเครื่องมือ "${result.toolCall.name}" ให้อัตโนมัติแล้ว ผลลัพธ์: ${JSON.stringify(
            result.toolCall.result,
          )}`,
        );
      }
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        // dev env นี้ยังไม่มีใครตั้งค่า BYOK Anthropic key จริง endpoint นี้จึงมีโอกาสสูงที่จะ error
        // ต้อง log ให้อ่านออกเสมอ ห้ามเงียบ
        if (err.status === 403) {
          appendLog("ปรึกษา AI ไม่สำเร็จ: DM เท่านั้นที่ใช้งานได้");
        } else {
          appendLog(`ปรึกษา AI ไม่สำเร็จ: HTTP ${err.status}`);
        }
      }
    },
  });

  const resolveProposal = useMutation({
    mutationFn: ({
      proposalId,
      action,
    }: {
      proposalId: string;
      action: "approve" | "reject";
    }) =>
      apiFetch<ResolveProposalResponse>(
        `/campaigns/${selectedCampaignId}/proposals/${proposalId}/resolve`,
        { method: "POST", token, body: { action } },
      ),
    onSuccess: (result, variables) => {
      // list เป็น pending-only เมื่อ resolve สำเร็จคือไม่ pending อีกต่อไปแน่นอน ตัดออกจาก cache ตรง ๆ
      // ด้วย setQueryData ดีกว่า invalidateQueries เพราะไม่ต้อง round-trip fetch ใหม่ และให้พฤติกรรม
      // สอดคล้องกับฝั่ง realtime merge (onProposalCreated) ที่ใช้ setQueryData เหมือนกัน
      queryClient.setQueryData<Proposal[]>(["proposals", selectedSessionId], (old) =>
        removeProposal(old, variables.proposalId),
      );
      appendLog(
        `${variables.action === "approve" ? "อนุมัติ" : "ปฏิเสธ"} proposal สำเร็จ`,
      );
      if (variables.action === "approve" && result.content) {
        appendConsultMessage("ai", result.content);
      }
    },
    onError: (err, variables) => {
      if (err instanceof ApiError && err.status === 409) {
        // 409 = มีคนอื่น resolve ไปก่อนแล้ว (race) — ก็ไม่ pending อีกต่อไปเหมือนกัน ตัดออกจาก cache
        queryClient.setQueryData<Proposal[]>(["proposals", selectedSessionId], (old) =>
          removeProposal(old, variables.proposalId),
        );
        appendLog("proposal นี้ถูกจัดการไปแล้วโดยคนอื่น (ตัดออกจากคิวแล้ว)");
      } else if (err instanceof ApiError) {
        // error อื่น (เช่น ไม่มี BYOK key ตอน approve) ให้เก็บ item ไว้ในคิวเหมือนเดิม แค่ log
        appendLog(`จัดการ proposal ไม่สำเร็จ: HTTP ${err.status}`);
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
    onProposalCreated: (payload) => {
      const proposal = normalizeProposalCreated(payload);
      if (!proposal) {
        appendLog("ได้รับ ProposalCreated broadcast ที่ข้อมูลไม่ครบ ข้ามไป");
        return;
      }
      // ห้ามอ่าน selectedSessionId จาก closure ตรงนี้ตรง ๆ — callback นี้ถูกสมัครครั้งเดียวต่อ token
      // (ดู dep array [token] ใน use-campaign-hub.ts) ค่าที่ closure จับไว้จะเป็นค่าเก่าค้าง (stale)
      // ใช้ proposal.sessionId ของ payload เองเป็น key แทน ไม่ต้องอ่าน state สดเลย — broadcast กระจาย
      // ไปทั้งกลุ่มแคมเปญ (ไม่ใช่แค่เซสชันที่เลือกอยู่) จึงต้อง merge เข้า cache ของเซสชันเจ้าของ proposal
      // นั้นจริง ๆ เท่านั้น ไม่ใช่ session ที่ UI กำลังเลือกอยู่ตอนนี้
      queryClient.setQueryData<Proposal[]>(["proposals", proposal.sessionId], (old) =>
        mergeProposal(old, proposal),
      );
      appendLog(
        `📡 realtime ProposalCreated: ${proposal.toolName} (session ${proposal.sessionId.slice(0, 8)})`,
      );
    },
    // DM console ยังไม่ได้ใช้ HpUpdated ในสปรินต์นี้ (โฟกัสอยู่ที่ player console — ดู PlayerConsole.tsx)
    // แต่ CampaignHubEvents บังคับให้มี handler ครบทุก event จึงใส่ no-op ไว้อย่างตั้งใจ ไม่ใช่ลืม
    onHpUpdated: () => {},
  });

  const campaigns = campaignsQuery.data ?? [];
  const sessions = sessionsQuery.data ?? [];
  const characters = charactersQuery.data ?? [];
  const proposals = proposalsQuery.data ?? [];

  return (
    <main className="flex-1 p-6 space-y-6 max-w-3xl mx-auto w-full">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">DnDEz — DM console (proof of concept)</h1>
          <p className="text-sm opacity-70">user: {userId}</p>
        </div>
        <div className="flex items-center gap-3">
          {/* คนละหน้ากันจริง ๆ ไม่ใช่สลับ view ในหน้าเดียว — ดู PlayerConsole.tsx ว่าทำไมถึงแยก */}
          <Link
            href="/play"
            className="text-sm underline opacity-70 hover:opacity-100"
          >
            เข้าสู่มุมมองผู้เล่น
          </Link>
          <button
            onClick={logout}
            className="text-sm underline opacity-70 hover:opacity-100"
          >
            ออกจากระบบ
          </button>
        </div>
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

      {selectedCampaignId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">ตัวละคร</h2>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              const form = e.currentTarget;
              const input = form.elements.namedItem(
                "characterName",
              ) as HTMLInputElement;
              if (input.value.trim()) {
                createCharacter.mutate(input.value.trim());
                input.value = "";
              }
            }}
            className="flex gap-2"
          >
            <input
              name="characterName"
              placeholder="ชื่อตัวละครใหม่"
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <button
              type="submit"
              disabled={createCharacter.isPending}
              className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
            >
              สร้าง
            </button>
          </form>
          <ul className="space-y-1">
            {characters.map((c) => (
              <li key={c.id} className="px-3 py-1.5">
                {c.name}{" "}
                <span className="opacity-50 text-xs">(level {c.level})</span>
                {c.controlledByDm && (
                  <span className="ml-1 text-xs text-indigo-500">
                    [DM คุม]
                  </span>
                )}
              </li>
            ))}
            {characters.length === 0 && (
              <li className="opacity-50 text-sm">ยังไม่มีตัวละคร</li>
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

      {selectedSessionId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">ปรึกษา AI</h2>
          <div className="flex gap-2">
            <textarea
              value={consultInput}
              onChange={(e) => setConsultInput(e.target.value)}
              placeholder="ถาม/สั่ง DM AI..."
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <button
              onClick={() => {
                const text = consultInput.trim();
                if (!text) return;
                // แสดงข้อความของ DM ใน transcript ทันที ไม่ต้องรอ response — mutation's onSuccess/onError
                // จะ log ฝั่ง AI/system เพิ่มเข้ามาเองเมื่อ resolve
                appendConsultMessage("dm", text);
                consult.mutate(text);
                setConsultInput("");
              }}
              disabled={consult.isPending || !consultInput.trim()}
              className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
            >
              ส่ง
            </button>
          </div>
          <ul className="space-y-1 text-sm max-h-64 overflow-y-auto">
            {consultMessages.map((m, i) => (
              <li
                key={i}
                className={
                  m.role === "dm"
                    ? "opacity-100"
                    : m.role === "ai"
                      ? "opacity-90 text-indigo-400"
                      : "opacity-60 italic"
                }
              >
                <span className="opacity-50">{m.at}</span>{" "}
                <span className="font-mono text-xs">[{m.role}]</span> {m.text}
              </li>
            ))}
            {consultMessages.length === 0 && (
              <li className="opacity-50 text-sm">ยังไม่มีบทสนทนา</li>
            )}
          </ul>
        </section>
      )}

      {selectedSessionId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">คิว proposal รออนุมัติ</h2>
          {proposalsQuery.isError && (
            <p className="text-sm text-red-500">โหลดคิว proposal ไม่สำเร็จ</p>
          )}
          <ul className="space-y-2">
            {proposals.map((p) => {
              // ตัด item นี้ออกจากการ disable ระหว่างที่ตัวอื่นในคิวกำลัง resolve อยู่ — ไม่งั้นทั้งคิวจะ
              // ล็อกพร้อมกันเวลามีคนกด approve/reject หลายอันติดกัน
              const isResolving =
                resolveProposal.isPending &&
                resolveProposal.variables?.proposalId === p.id;
              return (
                <li
                  key={p.id}
                  className="rounded border border-black/10 dark:border-white/10 p-2 space-y-1"
                >
                  <div>
                    <span className="font-medium">{p.toolName}</span>{" "}
                    <span className="opacity-50 text-xs">({p.subsystem})</span>
                  </div>
                  {/* ตั้งใจ render generic ด้วย JSON.stringify แทนการ special-case ต่อ tool — จะมี tool
                      เพิ่มเข้ามาอีกในอนาคต ไม่อยากผูก UI กับ shape เฉพาะของ roll_dice */}
                  <div className="font-mono text-xs opacity-80">
                    {JSON.stringify(p.toolInput)}
                  </div>
                  {p.reason && (
                    <div className="text-sm opacity-70">{p.reason}</div>
                  )}
                  <div className="flex gap-2 pt-1">
                    <button
                      onClick={() =>
                        resolveProposal.mutate({ proposalId: p.id, action: "approve" })
                      }
                      disabled={isResolving}
                      className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
                    >
                      อนุมัติ
                    </button>
                    <button
                      onClick={() =>
                        resolveProposal.mutate({ proposalId: p.id, action: "reject" })
                      }
                      disabled={isResolving}
                      className="rounded bg-red-600/80 px-3 py-1.5 text-white disabled:opacity-50"
                    >
                      ปฏิเสธ
                    </button>
                  </div>
                </li>
              );
            })}
            {proposals.length === 0 && (
              <li className="opacity-50 text-sm">ไม่มี proposal ค้างอยู่</li>
            )}
          </ul>
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

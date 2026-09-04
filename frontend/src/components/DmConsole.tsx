"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch, ApiError } from "@/lib/api";
import { useAuthStore } from "@/lib/auth-store";
import { useCampaignWorkspace } from "@/lib/use-campaign-workspace";
import { useCombatActions } from "@/lib/use-combat-actions";
import type { ApiKeyInfo, CampaignMember } from "@/lib/campaign-types";
import {
  AttackCheckPanel,
  DeathSavePanel,
  EncounterPanel,
  TurnStatePanel,
} from "./combat-panels";
import {
  ActionFeedList,
  AutonomyDialPanel,
  CharacterRosterPanel,
  DicePanel,
  HubStatusPanel,
  LogPanel,
  SessionPanel,
  WorkspaceColumn,
  WorkspaceGrid,
} from "./campaign-panels";

const sectionClass =
  "rounded border border-black/10 dark:border-white/10 p-4 space-y-3";

type CampaignDetail = { id: string; name: string; joinCode: string | null };

/**
 * Sprint F งานที่ 6a — หน้าของ DM (`/campaigns/{id}/dm`)
 *
 * ต่างจากหน้าผู้เล่นตรงที่ DM เห็น/ทำได้: ตั้ง join code, ดูรายชื่อสมาชิก, สร้างเซสชัน, ปรับ autonomy
 * dial, ปรึกษา AI, อนุมัติ proposal — ส่วนช่องพิมพ์ action แทนตัวละครไม่มีที่นี่ (DM สั่งผ่าน
 * /consult และจะคุม NPC ผ่าน UI ของงานที่ 6b)
 *
 * ⚠️ การซ่อน/แสดงปุ่มที่นี่เป็นเรื่อง UX ล้วน ๆ ไม่ใช่ security — ทุก endpoint เช็คสิทธิ์เองที่ชั้น API
 * (ดู doc comment ของ `useCampaignRole`)
 */
export function DmConsole({ campaignId }: { campaignId: string }) {
  const token = useAuthStore((s) => s.token);
  const queryClient = useQueryClient();
  const ws = useCampaignWorkspace(campaignId);
  const combat = useCombatActions({
    campaignId,
    sessionId: ws.selectedSessionId,
    appendLog: ws.appendLog,
  });
  const [npcVisible, setNpcVisible] = useState(true);
  const [joinCodeCopied, setJoinCodeCopied] = useState(false);

  const campaignQuery = useQuery({
    queryKey: ["campaign", campaignId],
    queryFn: () => apiFetch<CampaignDetail>(`/campaigns/${campaignId}`, { token }),
    enabled: !!token,
  });

  const membersQuery = useQuery({
    queryKey: ["campaign-members", campaignId],
    queryFn: () => apiFetch<CampaignMember[]>(`/campaigns/${campaignId}/members`, { token }),
    enabled: !!token,
  });

  const apiKeysQuery = useQuery({
    queryKey: ["api-keys"],
    queryFn: () => apiFetch<ApiKeyInfo[]>("/users/me/api-keys", { token }),
    enabled: !!token,
  });
  const anthropicKey = (apiKeysQuery.data ?? []).find((k) => k.provider === "anthropic");

  const setApiKey = useMutation({
    mutationFn: (apiKey: string) =>
      apiFetch<ApiKeyInfo>("/users/me/api-keys/anthropic", {
        method: "PUT",
        token,
        body: { apiKey },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["api-keys"] });
    },
    onError: (err) => {
      if (err instanceof ApiError) ws.appendLog(`ตั้งค่า API key ไม่สำเร็จ: HTTP ${err.status}`);
    },
  });

  const deleteApiKey = useMutation({
    mutationFn: (provider: string) =>
      apiFetch<void>(`/users/me/api-keys/${provider}`, { method: "DELETE", token }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["api-keys"] });
    },
    onError: (err) => {
      if (err instanceof ApiError) ws.appendLog(`ลบ API key ไม่สำเร็จ: HTTP ${err.status}`);
    },
  });

  const setJoinCode = useMutation({
    mutationFn: (joinCode: string | null) =>
      apiFetch<{ campaignId: string; joinCode: string | null }>(
        `/campaigns/${campaignId}/join-code`,
        { method: "PUT", token, body: { joinCode } },
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["campaign", campaignId] });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        // 409 = โค้ดนี้ถูกใช้โดยแคมเปญอื่นแล้ว (unique ข้ามทั้งระบบ) · 400 = ผิดรูปแบบ
        if (err.status === 409) {
          ws.appendLog("ตั้ง join code ไม่สำเร็จ: โค้ดนี้ถูกใช้ในแคมเปญอื่นแล้ว");
        } else if (err.status === 400) {
          ws.appendLog("ตั้ง join code ไม่สำเร็จ: ใช้ได้เฉพาะ A-Z 0-9 - _ ยาว 4-32 ตัว");
        } else {
          ws.appendLog(`ตั้ง join code ไม่สำเร็จ: HTTP ${err.status}`);
        }
      }
    },
  });

  // Sprint F งานที่ 6b — NPC/monster (งานที่ 7 ทำ backend ไว้ แต่ไม่เคยมี UI)
  const createNpc = useMutation({
    mutationFn: ({ name, statsVisibleToParty }: { name: string; statsVisibleToParty: boolean }) =>
      apiFetch<{ id: string }>(`/campaigns/${campaignId}/characters`, {
        method: "POST",
        token,
        body: { name, isNpc: true, statsVisibleToParty },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["characters", campaignId] });
    },
    onError: (err) => {
      if (err instanceof ApiError) ws.appendLog(`สร้าง NPC ไม่สำเร็จ: HTTP ${err.status}`);
    },
  });

  const toggleNpcVisibility = useMutation({
    mutationFn: ({ characterId, visible }: { characterId: string; visible: boolean }) =>
      apiFetch<{ characterId: string; visible: boolean }>(
        `/campaigns/${campaignId}/characters/${characterId}/npc-visibility`,
        { method: "PUT", token, body: { visible } },
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["characters", campaignId] });
    },
    onError: (err) => {
      if (err instanceof ApiError) ws.appendLog(`สลับการมองเห็น stat ไม่สำเร็จ: HTTP ${err.status}`);
    },
  });

  const npcs = ws.characters.filter((c) => c.controlledByDm);

  /**
   * navigator.clipboard ต้องการ secure context (https หรือ localhost) — ตอนเทสข้าม LAN ด้วย
   * http://192.168.x.x จะไม่มี API นี้เลย จึงต้อง fallback ไปใช้ execCommand("copy") แบบเก่า
   * ไม่ log ค่า joinCode เอง (เป็น credential เข้าโต๊ะ) ทั้งตอนสำเร็จและ error
   */
  async function copyJoinCode(code: string) {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(code);
      } else {
        const textarea = document.createElement("textarea");
        textarea.value = code;
        textarea.style.position = "fixed";
        textarea.style.opacity = "0";
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        document.execCommand("copy");
        document.body.removeChild(textarea);
      }
      setJoinCodeCopied(true);
      setTimeout(() => setJoinCodeCopied(false), 1500);
    } catch {
      ws.appendLog("คัดลอกโค้ดไม่สำเร็จ — คัดลอกด้วยตัวเองแทน");
    }
  }

  return (
    <>
      <HubStatusPanel hubStatus={ws.hubStatus} joinedCampaignId={ws.joinedCampaignId} />

      <WorkspaceGrid>
        {/* คอลัมน์ 1 — ตั้งค่าโต๊ะ: ของที่ตั้งครั้งเดียวแล้วแทบไม่แตะอีกระหว่างเล่น */}
        <WorkspaceColumn>
          <section className={sectionClass}>
            <h2 className="font-medium">โค้ดเข้าร่วม (join code)</h2>
            <p className="text-xs opacity-60">
              ผู้เล่นเอาโค้ดนี้ไปกรอกที่หน้าแรกเพื่อเข้าโต๊ะ — เห็นได้เฉพาะ DM เท่านั้น
            </p>
            <p className="text-sm flex items-center gap-2">
              ปัจจุบัน:{" "}
              {campaignQuery.data?.joinCode ? (
                <>
                  <span className="font-mono text-base">{campaignQuery.data.joinCode}</span>
                  <button
                    type="button"
                    onClick={() => copyJoinCode(campaignQuery.data!.joinCode!)}
                    className="rounded border border-black/20 dark:border-white/20 px-2 py-0.5 text-xs hover:bg-black/5 dark:hover:bg-white/5"
                  >
                    {joinCodeCopied ? "คัดลอกแล้ว" : "คัดลอก"}
                  </button>
                </>
              ) : (
                <span className="opacity-50">ยังไม่ได้ตั้ง (ผู้เล่นเข้าร่วมไม่ได้)</span>
              )}
            </p>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                const input = e.currentTarget.elements.namedItem("joinCode") as HTMLInputElement;
                if (input.value.trim()) {
                  setJoinCode.mutate(input.value.trim());
                  input.value = "";
                }
              }}
              className="flex gap-2"
            >
              <input
                name="joinCode"
                placeholder="เช่น OWLBEAR-TUESDAY"
                className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
              />
              <button
                type="submit"
                disabled={setJoinCode.isPending}
                className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
              >
                ตั้งโค้ด
              </button>
              {campaignQuery.data?.joinCode && (
                <button
                  type="button"
                  onClick={() => setJoinCode.mutate(null)}
                  disabled={setJoinCode.isPending}
                  className="rounded bg-red-600/80 px-3 py-1.5 text-white disabled:opacity-50"
                >
                  ปิดรับ
                </button>
              )}
            </form>
          </section>

          <section className={sectionClass}>
            <h2 className="font-medium">สมาชิกในโต๊ะ</h2>
            <ul className="space-y-1 text-sm">
              {(membersQuery.data ?? []).map((m) => (
                <li key={m.userId} className="px-3 py-1.5">
                  {m.displayName}{" "}
                  <span
                    className={
                      m.role === "Dm" ? "text-xs text-indigo-500" : "text-xs opacity-50"
                    }
                  >
                    [{m.role}]
                  </span>
                </li>
              ))}
              {(membersQuery.data ?? []).length === 0 && (
                <li className="opacity-50">ยังไม่มีสมาชิก</li>
              )}
            </ul>
          </section>

          <section className={sectionClass}>
            <h2 className="font-medium">Anthropic API key ของฉัน (BYOK)</h2>
            <p className="text-xs opacity-60">
              orchestrator จะดึงคีย์นี้ไปเรียก AI แทนคุณ — เป็นคีย์ระดับบัญชี ไม่ผูกกับแคมเปญใดแคมเปญหนึ่ง
            </p>
            {anthropicKey ? (
              <div className="flex items-center justify-between gap-2 text-sm">
                <span>
                  anthropic ····{anthropicKey.keySuffix}{" "}
                  <span
                    className={
                      anthropicKey.status === "Active" ? "text-green-500" : "text-red-500"
                    }
                  >
                    ({anthropicKey.status})
                  </span>
                </span>
                <button
                  onClick={() => deleteApiKey.mutate("anthropic")}
                  disabled={deleteApiKey.isPending}
                  className="rounded bg-red-600/80 px-3 py-1.5 text-white disabled:opacity-50"
                >
                  ลบ
                </button>
              </div>
            ) : (
              <p className="text-sm opacity-50">ยังไม่ได้ตั้งค่า API key</p>
            )}
            <form
              onSubmit={(e) => {
                e.preventDefault();
                const input = e.currentTarget.elements.namedItem("apiKey") as HTMLInputElement;
                if (input.value.trim()) {
                  setApiKey.mutate(input.value.trim());
                  input.value = "";
                }
              }}
              className="flex gap-2"
            >
              <input
                name="apiKey"
                type="password"
                placeholder={anthropicKey ? "ตั้งคีย์ใหม่ (rotate)..." : "sk-ant-..."}
                className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
              />
              <button
                type="submit"
                disabled={setApiKey.isPending}
                className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
              >
                บันทึก
              </button>
            </form>
          </section>

          <SessionPanel
            sessions={ws.sessions}
            selectedSessionId={ws.selectedSessionId}
            onSelect={ws.setSelectedSessionId}
            onCreate={() => ws.createSession.mutate()}
            creating={ws.createSession.isPending}
          />

          <CharacterRosterPanel
            title="ตัวละครในโต๊ะ"
            characters={ws.characters}
            onCreate={(name) => ws.createCharacter.mutate(name)}
            creating={ws.createCharacter.isPending}
          />

          <section className={sectionClass}>
            <h2 className="font-medium">NPC / มอนสเตอร์</h2>
            <p className="text-xs opacity-60">
              NPC ไม่มีเจ้าของเป็นผู้เล่น — DM สั่งได้คนเดียว · ปิดการมองเห็น stat ได้เพื่อไม่ให้ผู้เล่นรู้
              AC/HP ที่แน่นอน (ค่าเริ่มต้นคือเห็นได้)
            </p>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                const input = e.currentTarget.elements.namedItem("npcName") as HTMLInputElement;
                if (input.value.trim()) {
                  createNpc.mutate({ name: input.value.trim(), statsVisibleToParty: npcVisible });
                  input.value = "";
                }
              }}
              className="flex flex-wrap items-center gap-2"
            >
              <input
                name="npcName"
                placeholder="ชื่อ NPC/มอนสเตอร์"
                className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
              />
              <label className="flex items-center gap-1 text-xs opacity-70">
                <input
                  type="checkbox"
                  checked={npcVisible}
                  onChange={(e) => setNpcVisible(e.target.checked)}
                />
                ผู้เล่นเห็น stat
              </label>
              <button
                type="submit"
                disabled={createNpc.isPending}
                className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
              >
                สร้าง NPC
              </button>
            </form>
            <ul className="space-y-1 text-sm">
              {npcs.map((n) => (
                <li key={n.id} className="flex items-center justify-between px-3 py-1.5">
                  <span>
                    {n.name}{" "}
                    <span className="text-xs opacity-60">
                      {n.currentHp ?? "?"}/{n.maxHp ?? "?"} HP
                    </span>
                  </span>
                  <button
                    onClick={() =>
                      toggleNpcVisibility.mutate({ characterId: n.id, visible: false })
                    }
                    disabled={toggleNpcVisibility.isPending}
                    className="rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5 disabled:opacity-50"
                  >
                    ซ่อน stat
                  </button>
                </li>
              ))}
              {npcs.length === 0 && <li className="opacity-50">ยังไม่มี NPC</li>}
            </ul>
          </section>
        </WorkspaceColumn>

        {/* คอลัมน์ 2 — สนามต่อสู้: ของที่แตะบ่อยที่สุดตอนเล่นจริง */}
        <WorkspaceColumn>
          {!ws.selectedSessionId && (
            <section className={sectionClass}>
              <h2 className="font-medium">สนามต่อสู้</h2>
              <p className="text-sm opacity-50">
                เลือกเซสชัน (หรือสร้างใหม่) ทางซ้ายก่อน ถึงจะเปิด encounter และทอยได้
              </p>
            </section>
          )}
          {ws.selectedSessionId && (
            <>
              <EncounterPanel
                combat={combat}
                characters={ws.characters}
                canControlEncounter
              />
              <TurnStatePanel combat={combat} />
              <DeathSavePanel
                combat={combat}
                characters={ws.characters}
                isDm
                userId={ws.userId}
              />
              <AttackCheckPanel
                combat={combat}
                characters={ws.characters}
                actableCharacters={npcs}
              />

              <DicePanel
                expression={ws.rollExpression}
                setExpression={ws.setRollExpression}
                onRoll={() => ws.rollDice.mutate()}
                rolling={ws.rollDice.isPending}
              />

              <section className={sectionClass}>
                <h2 className="font-medium">Action feed (สิ่งที่ผู้เล่นทำแบบ realtime)</h2>
                <ActionFeedList actionFeed={ws.actionFeed} />
              </section>
            </>
          )}
        </WorkspaceColumn>

        {/* คอลัมน์ 3 — AI: dial คุมอำนาจ + ของที่ AI เสนอมารออนุมัติ */}
        <WorkspaceColumn>
          <AutonomyDialPanel
            settings={ws.autonomySettings}
            isError={ws.autonomySettingsQuery.isError}
            editable
            setLevel={ws.setAutonomyLevel}
          />

          {ws.selectedSessionId && (
            <>
              <section className={sectionClass}>
                <h2 className="font-medium">ปรึกษา AI</h2>
                <div className="flex gap-2">
                  <textarea
                    value={ws.consultInput}
                    onChange={(e) => ws.setConsultInput(e.target.value)}
                    placeholder="ถาม/สั่ง DM AI..."
                    className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
                  />
                  <button
                    onClick={() => {
                      const text = ws.consultInput.trim();
                      if (!text) return;
                      // แสดงข้อความของ DM ทันที ไม่ต้องรอ response — onSuccess/onError จะเติมฝั่ง AI ตามมา
                      ws.appendConsultMessage("dm", text);
                      ws.consult.mutate(text);
                      ws.setConsultInput("");
                    }}
                    disabled={ws.consult.isPending || !ws.consultInput.trim()}
                    className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
                  >
                    ส่ง
                  </button>
                </div>
                <ul className="space-y-1 text-sm max-h-64 overflow-y-auto">
                  {ws.consultMessages.map((m, i) => (
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
                  {ws.consultMessages.length === 0 && (
                    <li className="opacity-50 text-sm">ยังไม่มีบทสนทนา</li>
                  )}
                </ul>
              </section>

              <section className={sectionClass}>
                <h2 className="font-medium">คิว proposal รออนุมัติ</h2>
                {ws.proposalsQuery.isError && (
                  <p className="text-sm text-red-500">โหลดคิว proposal ไม่สำเร็จ</p>
                )}
                <ul className="space-y-2">
                  {ws.proposals.map((p) => {
                    // disable เฉพาะ item ที่กำลัง resolve อยู่ ไม่ล็อกทั้งคิว
                    const isResolving =
                      ws.resolveProposal.isPending &&
                      ws.resolveProposal.variables?.proposalId === p.id;
                    return (
                      <li
                        key={p.id}
                        className="rounded border border-black/10 dark:border-white/10 p-2 space-y-1"
                      >
                        <div>
                          <span className="font-medium">{p.toolName}</span>{" "}
                          <span className="opacity-50 text-xs">({p.subsystem})</span>
                        </div>
                        {/* generic ด้วย JSON.stringify โดยตั้งใจ — จะมี tool เพิ่มอีก ไม่ผูก UI กับ shape เดียว */}
                        <div className="font-mono text-xs opacity-80">
                          {JSON.stringify(p.toolInput)}
                        </div>
                        {p.reason && <div className="text-sm opacity-70">{p.reason}</div>}
                        <div className="flex gap-2 pt-1">
                          <button
                            onClick={() =>
                              ws.resolveProposal.mutate({ proposalId: p.id, action: "approve" })
                            }
                            disabled={isResolving}
                            className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
                          >
                            อนุมัติ
                          </button>
                          <button
                            onClick={() =>
                              ws.resolveProposal.mutate({ proposalId: p.id, action: "reject" })
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
                  {ws.proposals.length === 0 && (
                    <li className="opacity-50 text-sm">ไม่มี proposal ค้างอยู่</li>
                  )}
                </ul>
              </section>
            </>
          )}

          <LogPanel log={ws.log} />
        </WorkspaceColumn>
      </WorkspaceGrid>
    </>
  );
}

"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";
import { useAuthStore } from "@/lib/auth-store";
import { useCampaignWorkspace } from "@/lib/use-campaign-workspace";
import { useCombatActions } from "@/lib/use-combat-actions";
import type { CampaignMember } from "@/lib/campaign-types";
import {
  AttackCheckPanel,
  DeathSavePanel,
  EncounterPanel,
  LatestEventBar,
  TurnStatePanel,
  VitalsPanel,
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

/** ปุ่มคำสั่งพื้นฐาน — เป็นแค่ template ข้อความที่เติมลงช่อง free text ไม่ใช่เส้นทางแยก
 * (player-side-design.md §2 "ทั้งสองทางไปที่ orchestrator ทางเดียวกัน") */
const ACTION_TEMPLATES = [
  { label: "โจมตี", template: "ฉันโจมตี" },
  { label: "เคลื่อนที่", template: "ฉันเดินไปที่" },
  { label: "ปฏิสัมพันธ์", template: "ฉันลองใช้/ตรวจสอบ" },
];

/**
 * Sprint F งานที่ 6a — หน้าของผู้เล่น (`/campaigns/{id}/play`)
 *
 * ต่างจากหน้า DM: ไม่มีการตั้ง join code / API key / ปรึกษา AI / คิว proposal (endpoint พวกนั้น
 * เป็น DM-only ที่ชั้น API อยู่แล้ว การไม่แสดงที่นี่คือเรื่อง UX ไม่ใช่ตัวกันสิทธิ์) และเซสชันเลือกได้
 * แต่สร้างไม่ได้ — ผู้เล่นรอ DM เปิดเซสชันให้
 */
export function PlayerView({ campaignId }: { campaignId: string }) {
  const token = useAuthStore((s) => s.token);
  const ws = useCampaignWorkspace(campaignId);

  // ตัวละครที่กำลังโพสต์ action แทน — UI state ล้วน ๆ ของ panel นี้ ไม่ต้องเข้า store
  const [actingCharacterId, setActingCharacterId] = useState<string | null>(null);

  const combat = useCombatActions({
    campaignId,
    sessionId: ws.selectedSessionId,
    appendLog: ws.appendLog,
  });

  const membersQuery = useQuery({
    queryKey: ["campaign-members", campaignId],
    queryFn: () => apiFetch<CampaignMember[]>(`/campaigns/${campaignId}/members`, { token }),
    enabled: !!token,
  });

  return (
    <>
      <HubStatusPanel hubStatus={ws.hubStatus} joinedCampaignId={ws.joinedCampaignId} />

      <WorkspaceGrid>
        {/* คอลัมน์ 1 — โต๊ะ: เซสชันที่ DM เปิด + ใครอยู่ในโต๊ะบ้าง */}
        <WorkspaceColumn>
          <SessionPanel
            sessions={ws.sessions}
            selectedSessionId={ws.selectedSessionId}
            onSelect={ws.setSelectedSessionId}
          />

          <CharacterRosterPanel
            title="ตัวละครของฉัน"
            characters={ws.ownCharacters}
            onCreate={(name) => ws.createCharacter.mutate(name)}
            creating={ws.createCharacter.isPending}
            emptyLabel="ยังไม่มีตัวละครของคุณ — สร้างก่อนถึงจะส่ง action ได้"
          />

          <CharacterRosterPanel title="ทุกคนในโต๊ะ" characters={ws.characters} />

          <section className={sectionClass}>
            <h2 className="font-medium">สมาชิกในโต๊ะ</h2>
            <ul className="space-y-1 text-sm">
              {(membersQuery.data ?? []).map((m) => (
                <li key={m.userId} className="px-3 py-1.5">
                  {m.displayName}{" "}
                  <span
                    className={m.role === "Dm" ? "text-xs text-indigo-500" : "text-xs opacity-50"}
                  >
                    [{m.role}]
                  </span>
                </li>
              ))}
            </ul>
          </section>

        </WorkspaceColumn>

        {/* คอลัมน์ 2 — สนามต่อสู้ + ช่องส่ง action: ของที่ผู้เล่นใช้จริงตลอดเกม */}
        <WorkspaceColumn>
          {!ws.selectedSessionId && (
            <section className={sectionClass}>
              <h2 className="font-medium">สนามต่อสู้</h2>
              <p className="text-sm opacity-50">
                เลือกเซสชันทางซ้ายก่อน — ถ้ายังไม่มีให้รอ DM เปิดเซสชัน
              </p>
            </section>
          )}
          {ws.selectedSessionId && (
            <>
              <EncounterPanel
                combat={combat}
                characters={ws.characters}
                canControlEncounter={false}
              />
              <LatestEventBar combat={combat} characters={ws.characters} />
              <TurnStatePanel combat={combat} />
              <VitalsPanel combat={combat} characters={ws.characters} />
              <DeathSavePanel
                combat={combat}
                characters={ws.characters}
                isDm={false}
                userId={ws.userId}
              />
              <AttackCheckPanel
                combat={combat}
                characters={ws.characters}
                actableCharacters={ws.ownCharacters}
              />

              <DicePanel
                expression={ws.rollExpression}
                setExpression={ws.setRollExpression}
                onRoll={() => ws.rollDice.mutate()}
                rolling={ws.rollDice.isPending}
              />

              <section className={sectionClass}>
                <h2 className="font-medium">Action ของคุณ</h2>
                {ws.ownCharacters.length === 0 ? (
                  <p className="text-sm opacity-50">
                    ยังไม่มีตัวละครของคุณในแคมเปญนี้ — สร้างตัวละครก่อนถึงจะส่ง action ได้
                  </p>
                ) : (
                  <>
                    <select
                      value={actingCharacterId ?? ws.ownCharacters[0].id}
                      onChange={(e) => setActingCharacterId(e.target.value)}
                      className="w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
                    >
                      {ws.ownCharacters.map((c) => (
                        <option key={c.id} value={c.id}>
                          {c.name}
                        </option>
                      ))}
                    </select>
                    <div className="flex flex-wrap gap-2">
                      {ACTION_TEMPLATES.map((btn) => (
                        <button
                          key={btn.label}
                          type="button"
                          onClick={() => ws.setActionInput(btn.template)}
                          className="rounded border border-black/20 dark:border-white/20 px-2 py-1 text-xs hover:bg-black/5 dark:hover:bg-white/5"
                        >
                          {btn.label}
                        </button>
                      ))}
                    </div>
                    <div className="flex gap-2">
                      <textarea
                        value={ws.actionInput}
                        onChange={(e) => ws.setActionInput(e.target.value)}
                        placeholder="อธิบาย action ของตัวละคร..."
                        className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
                      />
                      <button
                        onClick={() => {
                          const text = ws.actionInput.trim();
                          const characterId = actingCharacterId ?? ws.ownCharacters[0]?.id;
                          if (!text || !characterId) return;
                          ws.postAction.mutate({ characterId, content: text });
                          ws.setActionInput("");
                        }}
                        disabled={ws.postAction.isPending || !ws.actionInput.trim()}
                        className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
                      >
                        ส่ง
                      </button>
                    </div>
                  </>
                )}
                <ActionFeedList actionFeed={ws.actionFeed} />
              </section>
            </>
          )}
        </WorkspaceColumn>

        {/* คอลัมน์ 3 — บริบทที่ดูอย่างเดียว: AI มีอำนาจแค่ไหน + log ของ client */}
        <WorkspaceColumn>
          <AutonomyDialPanel
            settings={ws.autonomySettings}
            isError={ws.autonomySettingsQuery.isError}
            editable={false}
          />

          <LogPanel log={ws.log} />
        </WorkspaceColumn>
      </WorkspaceGrid>
    </>
  );
}

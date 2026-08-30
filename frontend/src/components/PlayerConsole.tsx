"use client";

import Link from "next/link";
import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch, ApiError } from "@/lib/api";
import { useAuthStore } from "@/lib/auth-store";
import { useConsoleStore } from "@/lib/console-store";
import { useCampaignHub } from "@/lib/use-campaign-hub";
import {
  ownedCharacters,
  attackTargets,
  applyHpUpdate,
  describeCheckResult,
  describeAttackResult,
  normalizeHpUpdated,
  type CharacterSummary,
  type CharacterDetail,
  type CheckResult,
  type AttackResult,
} from "@/lib/player-actions";

type Advantage = "None" | "Advantage" | "Disadvantage";

// ค่า damage type ทั้ง 13 ตาม SRD (module-level เพราะไม่ผูกกับ state ของ component)
const DAMAGE_TYPES = [
  "Acid",
  "Bludgeoning",
  "Cold",
  "Fire",
  "Force",
  "Lightning",
  "Necrotic",
  "Piercing",
  "Poison",
  "Psychic",
  "Radiant",
  "Slashing",
  "Thunder",
] as const;

type Campaign = { id: string; name: string; mode: string; createdAt: string };
type GameSession = { id: string; mode: string; status: string; joinCode: string | null };

/**
 * Console ฝั่งผู้เล่น (Sprint E Part 3)
 *
 * แยกหน้า/แยก component ออกจาก DM console (CampaignConsole.tsx) โดยตั้งใจ เพื่อไม่ให้
 * เนื้อหาที่เฉพาะฝั่ง DM (เช่น proposal queue, AI-consult) รั่วมาทาง code path เดียวกัน
 * ซึ่งเป็นสาเหตุของบั๊ก Critical ที่เจอใน Sprint E Part 2 (realtime content ของ DM
 * หลุดไปถึงผู้เล่นผ่าน component ที่ใช้ร่วมกัน) — ที่นี่ยอมให้มี markup ซ้ำกับ DM console
 * แทนที่จะ extract เป็น shared component
 */
export function PlayerConsole() {
  const token = useAuthStore((s) => s.token);
  const userId = useAuthStore((s) => s.userId);
  const logout = useAuthStore((s) => s.logout);

  const selectedCampaignId = useConsoleStore((s) => s.selectedCampaignId);
  const selectedSessionId = useConsoleStore((s) => s.selectedSessionId);
  const joinedCampaignId = useConsoleStore((s) => s.joinedCampaignId);
  const hubStatus = useConsoleStore((s) => s.hubStatus);
  const log = useConsoleStore((s) => s.log);
  const setSelectedCampaignId = useConsoleStore((s) => s.setSelectedCampaignId);
  const setSelectedSessionId = useConsoleStore((s) => s.setSelectedSessionId);
  const setJoinedCampaignId = useConsoleStore((s) => s.setJoinedCampaignId);
  const setHubStatus = useConsoleStore((s) => s.setHubStatus);
  const appendLog = useConsoleStore((s) => s.appendLog);

  const [selectedCharacterId, setSelectedCharacterId] = useState<string | null>(null);
  const [checkExpression, setCheckExpression] = useState("1d20");
  const [checkAdvantage, setCheckAdvantage] = useState<Advantage>("None");
  const [checkDc, setCheckDc] = useState(10);
  const [attackExpression, setAttackExpression] = useState("1d20");
  const [attackAdvantage, setAttackAdvantage] = useState<Advantage>("None");
  const [targetCharacterId, setTargetCharacterId] = useState("");
  const [damageExpression, setDamageExpression] = useState("1d6");
  const [damageType, setDamageType] = useState("Slashing");

  const queryClient = useQueryClient();

  const campaignsQuery = useQuery({
    queryKey: ["campaigns"],
    queryFn: () => apiFetch<Campaign[]>("/campaigns", { token }),
    enabled: !!token,
  });

  const sessionsQuery = useQuery({
    queryKey: ["sessions", selectedCampaignId],
    queryFn: () => apiFetch<GameSession[]>(`/campaigns/${selectedCampaignId}/sessions`, { token }),
    enabled: !!token && !!selectedCampaignId,
  });

  const charactersQuery = useQuery({
    queryKey: ["characters", selectedCampaignId],
    queryFn: () =>
      apiFetch<CharacterSummary[]>(`/campaigns/${selectedCampaignId}/characters`, { token }),
    enabled: !!token && !!selectedCampaignId,
  });

  const characterDetailQuery = useQuery({
    queryKey: ["character-detail", selectedCharacterId],
    queryFn: () =>
      apiFetch<CharacterDetail>(
        `/campaigns/${selectedCampaignId}/characters/${selectedCharacterId}`,
        { token },
      ),
    enabled: !!token && !!selectedCampaignId && !!selectedCharacterId,
  });

  const campaigns = campaignsQuery.data ?? [];
  const sessions = sessionsQuery.data ?? [];
  const characters = charactersQuery.data ?? [];
  // filter นี้เป็นแค่ UX safeguard ให้ผู้เล่นเห็นเฉพาะตัวละครตัวเอง ไม่ใช่ security boundary —
  // backend ยังไม่ได้บังคับความเป็นเจ้าของตัวละครจริง (known pre-existing gap)
  const myCharacters = ownedCharacters(charactersQuery.data, userId);
  const targets = attackTargets(charactersQuery.data, selectedCharacterId);

  const check = useMutation({
    mutationFn: () =>
      apiFetch<CheckResult>(
        `/campaigns/${selectedCampaignId}/sessions/${selectedSessionId}/characters/${selectedCharacterId}/checks`,
        {
          method: "POST",
          body: { expression: checkExpression, advantage: checkAdvantage, dc: checkDc },
          token,
        },
      ),
    onSuccess: (result) => {
      appendLog(describeCheckResult(result));
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        appendLog(`ทอยเช็คไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  const attack = useMutation({
    mutationFn: () =>
      apiFetch<AttackResult>(
        `/campaigns/${selectedCampaignId}/sessions/${selectedSessionId}/characters/${selectedCharacterId}/attacks`,
        {
          method: "POST",
          // MVP ส่ง damage component เดียว (backend รับ 1-20) — multi-component เลื่อนไปก่อน
          body: {
            expression: attackExpression,
            advantage: attackAdvantage,
            targetCharacterId,
            damageComponents: [{ expression: damageExpression, type: damageType }],
          },
          token,
        },
      ),
    onSuccess: (result) => {
      const targetName = characters.find((c) => c.id === result.targetCharacterId)?.name ?? null;
      appendLog(describeAttackResult(result, targetName));
      if (result.targetNewHp !== null && result.targetNewTempHp !== null) {
        queryClient.setQueryData<CharacterDetail>(["character-detail", result.targetCharacterId], (old) =>
          applyHpUpdate(old, {
            characterId: result.targetCharacterId,
            previousHp: result.targetPreviousHp ?? result.targetNewHp!,
            newHp: result.targetNewHp!,
            previousTempHp: result.targetPreviousTempHp ?? result.targetNewTempHp!,
            newTempHp: result.targetNewTempHp!,
            droppedToZero: result.targetDroppedToZero ?? false,
            isInstantDeath: result.targetIsInstantDeath ?? false,
          }),
        );
      }
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        appendLog(`โจมตีไม่สำเร็จ: HTTP ${err.status}`);
      }
    },
  });

  useCampaignHub({
    token,
    selectedCampaignId,
    onStatusChange: setHubStatus,
    onJoinedChange: setJoinedCampaignId,
    onLog: appendLog,
    onDiceRolled: (payload) => {
      const e = payload as { expression: string; total: number; source: string };
      appendLog(`📡 realtime DiceRolled: ${e.expression} = ${e.total} (source=${e.source})`);
    },
    // ฝั่งผู้เล่นต้องไม่บริโภค broadcast ของ DM proposal เลย (no-op โดยตั้งใจ ไม่ใช่ลืม) —
    // นี่คือเหตุผลที่หน้านี้แยกจาก DM console
    onProposalCreated: () => {},
    onHpUpdated: (payload) => {
      // ห้ามอ่าน selectedCharacterId (หรือ React state อื่นจาก render) ใน callback นี้ —
      // callback ถูก register แค่ครั้งเดียวต่อ token (dep array ของ effect ใน hook คือ
      // [token]) ค่าที่ closure จับมาจาก render แรกจะกลายเป็นค่าเก่า (stale closure) การ
      // key setQueryData ด้วย event.characterId เท่านั้นคือสิ่งที่ทำให้ถูกต้องเสมอ — และ
      // applyHpUpdate เองก็ no-op อยู่แล้วถ้า cached id ไม่ตรงกับ event
      const event = normalizeHpUpdated(payload);
      if (!event) {
        appendLog("ได้รับ HpUpdated broadcast ที่ข้อมูลไม่ครบ ข้ามไป");
        return;
      }
      queryClient.setQueryData<CharacterDetail>(["character-detail", event.characterId], (old) =>
        applyHpUpdate(old, event),
      );
      appendLog(
        `📡 realtime HpUpdated: ${event.previousHp} → ${event.newHp} (character ${event.characterId.slice(0, 8)})`,
      );
    },
  });

  return (
    <main className="flex-1 p-6 space-y-6 max-w-3xl mx-auto w-full">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">DnDEz — มุมมองผู้เล่น</h1>
          <p className="text-sm opacity-70">user: {userId}</p>
        </div>
        <div className="flex items-center gap-3">
          <Link href="/" className="text-sm underline opacity-70 hover:opacity-100">
            ไปที่ DM console
          </Link>
          <button onClick={logout} className="text-sm underline opacity-70 hover:opacity-100">
            ออกจากระบบ
          </button>
        </div>
      </div>

      <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
        <h2 className="font-medium">
          SignalR:{" "}
          <span className={hubStatus === "connected" ? "text-green-500" : "text-yellow-500"}>
            {hubStatus}
          </span>
          {joinedCampaignId && <span className="opacity-70"> · joined {joinedCampaignId}</span>}
        </h2>
      </section>

      <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
        <h2 className="font-medium">แคมเปญ</h2>
        <ul className="space-y-1">
          {campaigns.length === 0 && <li className="opacity-50 text-sm">ยังไม่มีแคมเปญ</li>}
          {campaigns.map((c) => {
            const isSelected = c.id === selectedCampaignId;
            return (
              <li key={c.id}>
                <button
                  onClick={() => {
                    // เปลี่ยนแคมเปญแล้วตัวละคร/เป้าหมายเดิมอาจไม่มีอยู่จริงในแคมเปญใหม่ ต้อง reset
                    setSelectedCampaignId(c.id);
                    setSelectedCharacterId(null);
                    setTargetCharacterId("");
                  }}
                  className={`w-full text-left rounded px-3 py-1.5 ${isSelected ? "bg-indigo-600/20" : "hover:bg-black/5 dark:hover:bg-white/5"}`}
                >
                  {c.name} <span className="opacity-50 text-xs">({c.mode})</span>
                </button>
              </li>
            );
          })}
        </ul>
      </section>

      {selectedCampaignId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">เซสชัน</h2>
          <ul className="space-y-1">
            {sessions.length === 0 && <li className="opacity-50 text-sm">ยังไม่มีเซสชัน</li>}
            {sessions.map((s) => {
              const isSelected = s.id === selectedSessionId;
              return (
                <li key={s.id}>
                  <button
                    onClick={() => setSelectedSessionId(s.id)}
                    className={`w-full text-left rounded px-3 py-1.5 ${isSelected ? "bg-indigo-600/20" : "hover:bg-black/5 dark:hover:bg-white/5"}`}
                  >
                    {s.id.slice(0, 8)} — {s.mode} / {s.status}
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      {selectedCampaignId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">ตัวละครของฉัน</h2>
          {charactersQuery.isError && (
            <p className="text-sm text-red-500">โหลดรายชื่อตัวละครไม่สำเร็จ</p>
          )}
          <ul className="space-y-1">
            {myCharacters.length === 0 && (
              <li className="opacity-50 text-sm">ไม่มีตัวละครของคุณในแคมเปญนี้</li>
            )}
            {myCharacters.map((c) => {
              const isSelected = c.id === selectedCharacterId;
              return (
                <li key={c.id}>
                  <button
                    onClick={() => setSelectedCharacterId(c.id)}
                    className={`w-full text-left rounded px-3 py-1.5 ${isSelected ? "bg-indigo-600/20" : "hover:bg-black/5 dark:hover:bg-white/5"}`}
                  >
                    {c.name} <span className="opacity-50 text-xs">(level {c.level})</span>
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      {selectedCharacterId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">สถานะตัวละคร</h2>
          {characterDetailQuery.isPending && (
            <p className="opacity-50 text-sm">กำลังโหลด...</p>
          )}
          {characterDetailQuery.isError && (
            <p className="text-sm text-red-500">โหลดข้อมูลตัวละครไม่สำเร็จ</p>
          )}
          {characterDetailQuery.data && (
            <div className="space-y-2">
              <p className="font-medium">{characterDetailQuery.data.name}</p>
              <div className="flex items-end gap-6">
                <div>
                  <div className="text-xs opacity-60">HP</div>
                  <div className="text-3xl font-semibold">
                    {characterDetailQuery.data.currentHp} / {characterDetailQuery.data.maxHp}
                  </div>
                </div>
                <div>
                  <div className="text-xs opacity-60">Temp HP</div>
                  <div className="text-xl">{characterDetailQuery.data.currentTempHp}</div>
                </div>
                <div>
                  <div className="text-xs opacity-60">AC</div>
                  <div className="text-xl">{characterDetailQuery.data.armorClass}</div>
                </div>
              </div>
            </div>
          )}
        </section>
      )}

      {selectedSessionId && selectedCharacterId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">ทอยเช็ค (check)</h2>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              check.mutate();
            }}
            className="flex gap-2 flex-wrap"
          >
            <input
              value={checkExpression}
              onChange={(e) => setCheckExpression(e.target.value)}
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <select
              value={checkAdvantage}
              onChange={(e) => setCheckAdvantage(e.target.value as Advantage)}
              className="rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            >
              <option value="None">None</option>
              <option value="Advantage">Advantage</option>
              <option value="Disadvantage">Disadvantage</option>
            </select>
            <input
              type="number"
              min={1}
              max={40}
              value={checkDc}
              onChange={(e) => setCheckDc(Number(e.target.value))}
              className="w-24 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <button
              type="submit"
              disabled={check.isPending}
              className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
            >
              ทอยเช็ค
            </button>
          </form>
        </section>
      )}

      {selectedSessionId && selectedCharacterId && (
        <section className="rounded border border-black/10 dark:border-white/10 p-4 space-y-3">
          <h2 className="font-medium">โจมตี (attack)</h2>
          {targets.length === 0 && (
            <p className="opacity-50 text-sm">ไม่มีตัวละครอื่นให้เลือกเป็นเป้าหมาย</p>
          )}
          <form
            onSubmit={(e) => {
              e.preventDefault();
              attack.mutate();
            }}
            className="flex gap-2 flex-wrap"
          >
            <input
              value={attackExpression}
              onChange={(e) => setAttackExpression(e.target.value)}
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <select
              value={attackAdvantage}
              onChange={(e) => setAttackAdvantage(e.target.value as Advantage)}
              className="rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            >
              <option value="None">None</option>
              <option value="Advantage">Advantage</option>
              <option value="Disadvantage">Disadvantage</option>
            </select>
            <select
              value={targetCharacterId}
              onChange={(e) => setTargetCharacterId(e.target.value)}
              className="rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            >
              <option value="">— เลือกเป้าหมาย —</option>
              {targets.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
            <input
              value={damageExpression}
              onChange={(e) => setDamageExpression(e.target.value)}
              className="rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <select
              value={damageType}
              onChange={(e) => setDamageType(e.target.value)}
              className="rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            >
              {DAMAGE_TYPES.map((d) => (
                <option key={d} value={d}>
                  {d}
                </option>
              ))}
            </select>
            <button
              type="submit"
              disabled={attack.isPending || !targetCharacterId}
              className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
            >
              โจมตี
            </button>
          </form>
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

      {/*
        ยังไม่ทำช่องแชท/roleplay แบบ free-text เพราะ backend ยังไม่มี endpoint ให้ผู้เล่นโพสต์
        ข้อความ (/consult เป็นของ DM เท่านั้น) — ทำ text box ที่ไม่ persist ที่ไหนเลยคือ fake
        functionality เลื่อนไปจนกว่าจะมี messaging endpoint จริง
      */}
    </main>
  );
}

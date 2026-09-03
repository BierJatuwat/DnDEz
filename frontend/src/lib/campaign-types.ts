/**
 * Sprint F งานที่ 6a — type ที่เดิมประกาศไว้ใน CampaignConsole.tsx ไฟล์เดียว ย้ายออกมาเป็นไฟล์กลาง
 * ตอนแยกหน้า DM/Player เพราะทั้งสองหน้าใช้ร่วมกัน (ก่อนหน้านี้มีหน้าเดียวเลยไม่จำเป็น)
 *
 * ⚠️ ทุก type ในนี้ต้องตรงกับ response record ฝั่ง API เป๊ะ — ไม่ได้ generate อัตโนมัติ ถ้าแก้ฝั่ง
 * C# ต้องมาแก้ที่นี่ด้วยมือเสมอ
 */

export type Campaign = {
  id: string;
  name: string;
  mode: string;
  createdAt: string;
  ownerUserId: string;
};

export type GameSession = {
  id: string;
  mode: string;
  status: string;
  joinCode: string | null;
};

export type DieRollDetail = {
  sides: number;
  value: number;
  dropped: boolean;
  sign: number;
};

export type DiceRolledEvent = {
  id: string;
  sessionId: string;
  characterId: string | null;
  expression: string;
  rolls: DieRollDetail[];
  total: number;
  source: string;
};

/**
 * รายการตัวละครจาก GET เบากว่า response ตอน POST (ไม่มี AC/sheet เต็ม) — แยก type ตามจริง ไม่ปนกัน
 * HP fields เพิ่มมาตั้งแต่ Sprint E Part 3 เพื่อแสดง HP realtime โดยไม่ต้อง fetch detail ทีละตัว
 *
 * ⚠️ Sprint F งานที่ 7 — HP เป็น nullable แล้ว: `null` หมายถึง "ตัวนี้เป็น NPC ที่ DM ปิดการมองเห็น
 * stat ไว้ และเราไม่ใช่ DM" (ดู `CharacterStatsVisibility` ฝั่ง API) ไม่ใช่ "ไม่มีข้อมูล"
 */
export type CharacterSummary = {
  id: string;
  name: string;
  level: number;
  ownerUserId: string | null;
  controlledByDm: boolean;
  currentHp: number | null;
  maxHp: number | null;
  currentTempHp: number | null;
};

export type HpUpdatedEvent = {
  characterId: string;
  previousHp: number;
  newHp: number;
  previousTempHp: number;
  newTempHp: number;
  droppedToZero: boolean;
  isInstantDeath: boolean;
};

export type ToolCallResult = { name: string; input: unknown; result: unknown };

export type ConsultResponse = {
  messageId: string | null;
  content: string;
  toolCall: ToolCallResult | null;
  pending: { proposalId: string; toolName: string; toolInput: unknown } | null;
};

export type ResolveProposalResponse = {
  proposalId: string;
  status: string;
  messageId: string | null;
  content: string;
  toolCall: ToolCallResult | null;
};

/** /users/me/api-keys เป็น per-user ไม่ใช่ per-campaign (Sprint D) — user คนเดียวเป็น DM ได้หลายโต๊ะ */
export type ApiKeyInfo = {
  provider: string;
  keySuffix: string;
  status: string;
  createdAt: string;
  lastValidatedAt: string | null;
};

/** ต้องตรงกับ AutonomySubsystem/AutonomyLevel ฝั่ง Domain.Enums เป๊ะ (เทียบ string ตรง ๆ ไม่ผ่าน mapping) */
export type AutonomySetting = { campaignId: string; subsystem: string; level: string };

export type CampaignMember = {
  userId: string;
  displayName: string;
  role: string;
  joinedAt: string;
};

export const AUTONOMY_SUBSYSTEMS: { value: string; label: string }[] = [
  { value: "Narration", label: "การเล่าเรื่อง" },
  { value: "NpcDialogue", label: "บทพูด NPC" },
  { value: "RulesCombat", label: "กฎการต่อสู้" },
  { value: "MapScene", label: "ฉาก/แผนที่" },
];

export const AUTONOMY_LEVELS: { value: string; label: string }[] = [
  { value: "Manual", label: "ทำเอง (Manual)" },
  { value: "Suggest", label: "เสนอก่อน (Suggest)" },
  { value: "Autonomous", label: "อัตโนมัติ (Autonomous)" },
];

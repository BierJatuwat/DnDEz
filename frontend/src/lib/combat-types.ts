/**
 * Sprint F งานที่ 6b — type ของฝั่ง combat (encounter / turn order / death save / attack / check)
 * ต้องตรงกับ response record ฝั่ง API เป๊ะ (ไม่ได้ generate อัตโนมัติ)
 */

export type TurnState = {
  actionAvailable: boolean;
  bonusActionAvailable: boolean;
  reactionAvailable: boolean;
  movementRemainingFeet: number;
  maxMovementFeet: number;
};

export type EncounterParticipant = {
  id: string;
  characterId: string;
  characterName: string;
  initiativeRoll: number;
  initiativeModifier: number;
  initiativeTotal: number;
  turnOrder: number;
};

export type Encounter = {
  id: string;
  campaignId: string;
  sessionId: string;
  startedAt: string;
  endedAt: string | null;
  currentParticipantId: string | null;
  currentCharacterId: string | null;
  participants: EncounterParticipant[];
  turnState: TurnState | null;
};

export type NextTurnResult = {
  encounterId: string;
  currentParticipantId: string;
  currentCharacterId: string;
  turnOrder: number;
  turnState: TurnState;
};

export type SpendTurnResourceResult = {
  encounterId: string;
  participantId: string;
  characterId: string;
  resource: string;
  turnState: TurnState;
};

/** Status ตรงกับ Domain.Enums.DeathSaveStatus + "NotDying" ที่ API ใช้แทน "ไม่มีแถวในตาราง" */
export type DeathSaveState = {
  characterId: string;
  successes: number;
  failures: number;
  status: "NotDying" | "Rolling" | "Stable" | "Dead";
};

export type DeathSaveRollResult = {
  id: string;
  characterId: string;
  expression: string;
  total: number;
  outcome: string;
  successes: number;
  failures: number;
  status: string;
  revived: boolean;
  currentHp: number;
};

export type AttackResult = {
  id: string;
  attackerCharacterId: string;
  targetCharacterId: string;
  expression: string;
  total: number;
  /** null = NPC ที่ DM ซ่อน stat ไว้ และเราไม่ใช่ DM (Sprint F งานที่ 7) */
  targetArmorClass: number | null;
  outcome: string;
  hit: boolean;
  totalDamage: number | null;
  targetNewHp: number | null;
  targetDroppedToZero: boolean | null;
  targetIsInstantDeath: boolean | null;
};

export type CheckResult = {
  id: string;
  characterId: string;
  expression: string;
  total: number;
  dc: number;
  success: boolean;
};

/** ทรัพยากรที่ spend ได้ในหนึ่งเทิร์น — ต้องตรงกับที่ API รับ (ActionEconomyResolver) */
export const TURN_RESOURCES = [
  { value: "Action", label: "Action" },
  { value: "BonusAction", label: "Bonus action" },
  { value: "Reaction", label: "Reaction" },
  { value: "Movement", label: "Movement" },
] as const;

/** ชนิดดาเมจที่ฟอร์มโจมตีให้เลือก — subset ที่ใช้บ่อย (API รับครบ 13 ค่าตาม DnDEz.Rules.DamageType) */
export const COMMON_DAMAGE_TYPES = [
  "Slashing",
  "Piercing",
  "Bludgeoning",
  "Fire",
  "Cold",
  "Lightning",
  "Poison",
  "Radiant",
  "Necrotic",
] as const;

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
  /** นับทุกเทิร์นที่ผ่านไป (เริ่ม 0) ไม่ใช่เลข "รอบ" — ใช้ `roundOf()` แปลงเป็นรอบ */
  turnNumber: number;
  participants: EncounterParticipant[];
  turnState: TurnState | null;
};

/**
 * เลข "รอบ" แบบ D&D = ทุกคนได้เล่นครบหนึ่งรอบ · backend เก็บแค่ตัวนับเทิร์นรวม (`turnNumber`)
 * เพราะเป็นค่าที่ใช้เป็น Redis key ของ turn state อยู่แล้ว การหารตรงนี้จึงเป็นเรื่องของการแสดงผล
 * ล้วน ๆ ไม่ต้องเก็บซ้ำใน DB
 */
export function roundOf(encounter: Encounter): number {
  const count = encounter.participants.length;
  if (count === 0) return 1;
  return Math.floor(encounter.turnNumber / count) + 1;
}

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

/**
 * defect รอบ B (Layer 1) — ผลของ action ล่าสุดในรูปแบบที่ UI เอาไปเรนเดอร์เป็นประโยคได้
 *
 * เดิมผลทอยทุกอย่างถูกแปลงเป็นสตริงแล้วต่อท้าย log ทันที (`⚔️ Hit: 17 vs AC 15 → ดาเมจ 6`)
 * ข้อมูลที่มีโครงสร้างจึงหายไปตั้งแต่ต้นทาง แสดงเป็นแถบ "ล่าสุด" แบบในรูปที่ 3 ไม่ได้เลย
 *
 * ⚠️ ตอนนี้ครอบเฉพาะ action ที่ **ผู้ใช้คนนี้เป็นคนสั่งเอง** — ของคนอื่นในโต๊ะยังไม่เข้าแถบนี้
 * เพราะ backend ไม่มี broadcast ที่ส่งผลโจมตีเต็มรูป (มีแค่ HpUpdated ที่บอก HP ใหม่เฉย ๆ)
 * ถ้าจะให้เห็นของทั้งโต๊ะต้องเพิ่ม broadcast ฝั่ง API ก่อน
 */
export type LatestCombatEvent =
  | {
      kind: "attack";
      attackerId: string;
      targetId: string;
      total: number;
      hit: boolean;
      damage: number | null;
    }
  | {
      kind: "check";
      characterId: string;
      total: number;
      dc: number;
      success: boolean;
    }
  | {
      kind: "death-save";
      characterId: string;
      outcome: string;
      successes: number;
      failures: number;
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

export type DieRollDetail = { sides: number; value: number; dropped: boolean; sign: number };

export type CharacterSummary = {
  id: string;
  name: string;
  level: number;
  ownerUserId: string;
  controlledByDm: boolean;
};

export type CharacterDetail = {
  id: string;
  campaignId: string;
  name: string;
  level: number;
  sheet: unknown;
  ownerUserId: string;
  controlledByDm: boolean;
  harvestConsent: boolean;
  isOrphan: boolean;
  createdAt: string;
  currentHp: number;
  maxHp: number;
  currentTempHp: number;
  armorClass: number;
  damageResistances: string[];
  damageVulnerabilities: string[];
  damageImmunities: string[];
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

export type CheckResult = {
  id: string;
  characterId: string;
  expression: string;
  rolls: DieRollDetail[];
  total: number;
  dc: number;
  success: boolean;
};

export type DamageComponentResult = {
  type: string;
  rawAmount: number;
  adjustedAmount: number;
  rolls: DieRollDetail[];
};

// ทุกฟิลด์ที่เกี่ยวกับดาเมจ/HP เป็น null ตอน miss (backend ประกาศเป็น int?/bool?)
export type AttackResult = {
  id: string;
  attackerCharacterId: string;
  targetCharacterId: string;
  expression: string;
  rolls: DieRollDetail[];
  total: number;
  targetArmorClass: number;
  outcome: string;
  hit: boolean;
  damageComponents: DamageComponentResult[] | null;
  totalDamage: number | null;
  targetPreviousHp: number | null;
  targetNewHp: number | null;
  targetPreviousTempHp: number | null;
  targetNewTempHp: number | null;
  targetDroppedToZero: boolean | null;
  targetIsInstantDeath: boolean | null;
};

/**
 * broadcast `HpUpdated` มาจาก SignalR ซึ่งเป็น `unknown` จริง ๆ (ข้อมูลข้าม wire ไว้ใจไม่ได้)
 * เลย narrow แบบ runtime ทีละ field แทนการ cast — field ที่ต้องมีเพื่อระบุตัวละครและ HP ใหม่ได้คือ
 * characterId/newHp/newTempHp เท่านั้น ที่เหลือ (previousHp/previousTempHp/droppedToZero/isInstantDeath)
 * เป็นแค่ของประกอบการแสดงผล ยอมรับค่าที่ขาดหรือผิด type ได้โดยใส่ default ที่สมเหตุสมผลแทนการทิ้งทั้ง payload
 * (แนวเดียวกับ normalizeProposalCreated)
 */
export function normalizeHpUpdated(payload: unknown): HpUpdatedEvent | null {
  if (typeof payload !== "object" || payload === null) return null;

  const record = payload as Record<string, unknown>;
  const { characterId, newHp, newTempHp } = record;

  if (typeof characterId !== "string") return null;
  if (typeof newHp !== "number") return null;
  if (typeof newTempHp !== "number") return null;

  const previousHp = typeof record.previousHp === "number" ? record.previousHp : newHp;
  const previousTempHp =
    typeof record.previousTempHp === "number" ? record.previousTempHp : newTempHp;
  const droppedToZero = typeof record.droppedToZero === "boolean" ? record.droppedToZero : false;
  const isInstantDeath =
    typeof record.isInstantDeath === "boolean" ? record.isInstantDeath : false;

  return {
    characterId,
    previousHp,
    newHp,
    previousTempHp,
    newTempHp,
    droppedToZero,
    isInstantDeath,
  };
}

/**
 * เขียน HP/temp HP ใหม่ลงบน detail ที่ cache ไว้ — broadcast กระจายไปทั้งกลุ่มแคมเปญ ไม่ใช่แค่ตัวละคร
 * ที่กำลังเปิดดูอยู่ ดังนั้นถ้า id ไม่ตรงต้องคืน reference เดิม (กัน re-render เปล่าและกันเขียนทับผิดใบ)
 * และถ้ายังไม่มี cache เลยให้คืน undefined ตรง ๆ เพื่อให้ TanStack Query bail out จาก setQueryData
 * ปล่อยให้ fetch ที่กำลังทำงานอยู่เป็น source of truth แทน (แนวเดียวกับ mergeProposal)
 */
export function applyHpUpdate(
  existing: CharacterDetail | undefined,
  event: HpUpdatedEvent,
): CharacterDetail | undefined {
  if (existing === undefined) return undefined;

  if (existing.id !== event.characterId) return existing;

  return { ...existing, currentHp: event.newHp, currentTempHp: event.newTempHp };
}

/**
 * กรองเฉพาะตัวละครที่ user ล็อกอินเป็นเจ้าของ — เป็น UX safeguard เท่านั้น ไม่ใช่ security boundary
 * backend ยังไม่ enforce ความเป็นเจ้าของตัวละครบน endpoint พวกนี้ (ช่องโหว่ที่รู้อยู่แล้ว ตั้งใจเลื่อนแก้
 * มาตั้งแต่ Sprint A Part 3) ห้ามพึ่งตัวกรองนี้เพื่อ authorization เด็ดขาด
 */
export function ownedCharacters(
  list: CharacterSummary[] | undefined,
  userId: string | null,
): CharacterSummary[] {
  if (list === undefined || userId === null) return [];

  return list.filter((character) => character.ownerUserId === userId);
}

/**
 * รายชื่อเป้าหมายที่โจมตีได้ = ทุกคนยกเว้นตัวที่กำลังลงมือ ถ้ายังไม่รู้ว่าใครลงมือ (null) ก็ยังตัดใครไม่ได้
 * เลยคืนทั้ง list ไปก่อน
 */
export function attackTargets(
  list: CharacterSummary[] | undefined,
  actingCharacterId: string | null,
): CharacterSummary[] {
  if (list === undefined) return [];
  if (actingCharacterId === null) return list;

  return list.filter((character) => character.id !== actingCharacterId);
}

/**
 * สรุปผลเช็คเป็นบรรทัดเดียวอ่านง่าย สำหรับ log — ระวังคำว่า "ไม่สำเร็จ" ต้องไม่หลุดมาปนกับ
 * บรรทัดที่สำเร็จ เพราะ "ไม่สำเร็จ" มี "สำเร็จ" เป็น substring อยู่แล้ว (ทำสลับกันไม่ได้)
 */
export function describeCheckResult(result: CheckResult): string {
  const outcome = result.success ? "สำเร็จ" : "ไม่สำเร็จ";
  return `เช็ค ${result.expression} = ${result.total} vs DC ${result.dc} → ${outcome}`;
}

/**
 * สรุปผลโจมตีเป็นบรรทัดเดียวอ่านง่าย สำหรับ log — outcome เป็น string ธรรมดาจาก backend enum
 * (CriticalHit/Hit/Miss/AutomaticMiss) แต่ค่าที่ไม่รู้จักในอนาคตต้องไม่ทำให้พัง เลย fallback ไปพิมพ์
 * ค่าดิบแทนการ throw หรือเดาความหมายผิด ๆ
 */
export function describeAttackResult(result: AttackResult, targetName: string | null): string {
  const targetLabel = targetName ?? result.targetCharacterId;

  let outcomeText: string;
  switch (result.outcome) {
    case "CriticalHit":
      outcomeText = "คริติคอลฮิต";
      break;
    case "Hit":
      outcomeText = "โดน";
      break;
    case "Miss":
      outcomeText = "พลาด";
      break;
    case "AutomaticMiss":
      outcomeText = "พลาดอัตโนมัติ (ทอยได้ 1)";
      break;
    default:
      outcomeText = result.outcome;
  }

  let text = `โจมตี ${targetLabel}: ทอยได้ ${result.total} vs AC ${result.targetArmorClass} → ${outcomeText}`;

  if (result.totalDamage !== null) {
    text += ` ดาเมจ ${result.totalDamage}`;
    if (result.targetPreviousHp !== null && result.targetNewHp !== null) {
      text += ` (HP ${result.targetPreviousHp} → ${result.targetNewHp})`;
    }
  }

  if (result.targetDroppedToZero === true) {
    text += " (ล้มลง)";
  }

  if (result.targetIsInstantDeath === true) {
    text += " ตายทันที";
  }

  return text;
}

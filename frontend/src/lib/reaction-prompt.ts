/**
 * Sprint G Part 10 (ส่วน ง) — ตรรกะของ prompt "จะสวนไหม" แยกจาก React ให้เทสต์ได้ตรง ๆ
 * ตามธรรมเนียมเดิมของโปรเจกต์ (lib/ มีเทสต์ · component เป็นการนำเสนออย่างเดียว)
 */

export interface ReactionPrompt {
  reactionId: string;
  sessionId: string;
  moverCharacterId: string;
  threatCharacterId: string;
  threatName: string;
  timeoutSeconds: number;
  /** เวลาหมดอายุแบบสัมบูรณ์จาก server (ISO-8601) */
  deadlineUtc: string;
}

/**
 * แปลง payload ดิบจาก SignalR เป็น prompt ที่ใช้ได้ — คืน null ถ้ารูปร่างไม่ครบ
 *
 * narrow แบบไม่ไว้ใจ payload เลย (แพทเทิร์นเดียวกับ `proposal-queue.ts` และ `action-feed.ts`)
 * เพราะ payload มาจาก network ไม่ใช่จากโค้ดเรา — ฟิลด์หายแล้วปล่อยผ่านจะได้ prompt ที่กดแล้วพัง
 */
export function normalizeReactionPrompt(payload: unknown): ReactionPrompt | null {
  if (typeof payload !== "object" || payload === null) return null;
  const p = payload as Record<string, unknown>;

  const reactionId = p.reactionId;
  const sessionId = p.sessionId;
  const moverCharacterId = p.moverCharacterId;
  const threatCharacterId = p.threatCharacterId;
  const threatName = p.threatName;
  const timeoutSeconds = p.timeoutSeconds;
  const deadlineUtc = p.deadlineUtc;

  if (
    typeof reactionId !== "string" ||
    typeof sessionId !== "string" ||
    typeof moverCharacterId !== "string" ||
    typeof threatCharacterId !== "string" ||
    typeof threatName !== "string" ||
    typeof timeoutSeconds !== "number" ||
    typeof deadlineUtc !== "string"
  ) {
    return null;
  }

  return {
    reactionId,
    sessionId,
    moverCharacterId,
    threatCharacterId,
    threatName,
    timeoutSeconds,
    deadlineUtc,
  };
}

/**
 * วินาทีที่เหลือก่อนหมดสิทธิ์ — คิดจาก **deadline สัมบูรณ์ของ server** ไม่ใช่นับถอยหลังจาก
 * timeoutSeconds ฝั่ง client
 *
 * เหตุผล: ถ้านับเองจากตอนได้รับ event เวลาจะเพี้ยนตาม network latency และจะเพี้ยนหนักมากถ้าผู้ใช้
 * เพิ่งต่อกลับมาหลังหลุด (client จะนับใหม่เต็มจำนวนทั้งที่ server ใกล้หมดเวลาแล้ว) — ตัวเลขบนจอ
 * ต้องตรงกับตัวที่ sweeper ใช้ตัดสินจริง ไม่งั้นผู้ใช้จะเห็น "เหลือ 12 วินาที" แล้วโดนตัดสิทธิ์ทันที
 *
 * ปัดขึ้นเพื่อให้ยังเห็น "1" ในวินาทีสุดท้าย แทนที่จะกระโดดจาก 1 ไป 0 ตั้งแต่ยังเหลือเวลาอยู่จริง
 */
export function secondsRemaining(deadlineUtc: string, now: Date): number {
  const deadline = Date.parse(deadlineUtc);
  if (Number.isNaN(deadline)) return 0;
  return Math.max(0, Math.ceil((deadline - now.getTime()) / 1000));
}

/** หมดเวลาแล้วหรือยัง — ใช้ซ่อน prompt ฝั่ง client โดยไม่ต้องรอ server ตอบกลับ */
export function isExpired(deadlineUtc: string, now: Date): boolean {
  return secondsRemaining(deadlineUtc, now) <= 0;
}

/**
 * ระดับความเร่งด่วนสำหรับเลือกสีแถบนับถอยหลัง — คิดเป็นสัดส่วนของเวลาที่ตั้งไว้ ไม่ใช่วินาทีคงที่
 * เพราะ DM ตั้ง timeout ได้ตั้งแต่ 5 ถึง 300 วินาที ("เหลือ 5 วินาที" แปลว่าคนละอย่างกันมากระหว่าง
 * timeout 10 วิ กับ 300 วิ
 */
export function urgency(remaining: number, timeoutSeconds: number): "calm" | "hurry" | "critical" {
  if (timeoutSeconds <= 0) return "critical";
  const ratio = remaining / timeoutSeconds;
  if (ratio <= 0.25) return "critical";
  if (ratio <= 0.5) return "hurry";
  return "calm";
}

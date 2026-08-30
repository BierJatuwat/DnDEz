export type ActionMessage = {
  id: string;
  sessionId: string;
  authorType: string;
  characterId: string | null;
  content: string;
  createdAt: string;
};

/**
 * broadcast `ActionMessageCreated` มาจาก SignalR ซึ่งเป็น `unknown` จริง ๆ (ข้อมูลข้าม wire ไว้ใจ
 * ไม่ได้) เลย narrow แบบ runtime ทีละ field เหมือน normalizeProposalCreated — field ที่ต้องมีคือ
 * messageId/sessionId/authorType/content เท่านั้น characterId ยอมรับ null ได้ (narration ของ AI
 * ไม่ผูก character)
 */
export function normalizeActionMessageCreated(payload: unknown): ActionMessage | null {
  if (typeof payload !== "object" || payload === null) return null;

  const record = payload as Record<string, unknown>;
  const { messageId, sessionId, authorType, content } = record;

  if (typeof messageId !== "string") return null;
  if (typeof sessionId !== "string") return null;
  if (typeof authorType !== "string") return null;
  if (typeof content !== "string") return null;

  const characterId = typeof record.characterId === "string" ? record.characterId : null;
  const createdAt = typeof record.createdAt === "string" ? record.createdAt : new Date().toISOString();

  return {
    id: messageId,
    sessionId,
    authorType,
    characterId,
    content,
    createdAt,
  };
}

const MAX_FEED_LENGTH = 50;

/**
 * เติมข้อความใหม่ท้าย feed (เรียงเก่า -> ใหม่ เหมือน consultMessages ใน console-store) dedupe
 * ด้วย id + cap ความยาวไว้ที่ MAX_FEED_LENGTH (pattern เดียวกับ console-store.appendConsultMessage)
 * กัน feed ยาวไม่จำกัดถ้า session เปิดค้างไว้นาน ๆ
 */
export function appendActionMessage(
  existing: ActionMessage[],
  incoming: ActionMessage,
): ActionMessage[] {
  if (existing.some((m) => m.id === incoming.id)) return existing;

  return [...existing, incoming].slice(-MAX_FEED_LENGTH);
}

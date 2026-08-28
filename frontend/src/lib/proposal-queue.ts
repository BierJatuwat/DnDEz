export type Proposal = {
  id: string;
  sessionId: string;
  subsystem: string;
  toolName: string;
  toolInput: unknown;
  reason: string | null;
  status: string;
  createdAt: string;
};

/**
 * broadcast `ProposalCreated` มาจาก SignalR ซึ่งเป็น `unknown` จริง ๆ (ข้อมูลข้าม wire ไว้ใจไม่ได้)
 * เลย narrow แบบ runtime ทีละ field แทนการ cast — field ที่ต้องมีเพื่อให้ item ใช้งานได้จริงคือ
 * proposalId/sessionId/toolName เท่านั้น ที่เหลือ (subsystem/reason/toolInput) ยอมรับค่าที่ขาดหายได้
 * โดยใส่ default ที่สมเหตุสมผลแทนการทิ้งทั้ง payload
 */
export function normalizeProposalCreated(
  payload: unknown,
  now: string = new Date().toISOString(),
): Proposal | null {
  if (typeof payload !== "object" || payload === null) return null;

  const record = payload as Record<string, unknown>;
  const { proposalId, sessionId, toolName } = record;

  if (typeof proposalId !== "string") return null;
  if (typeof sessionId !== "string") return null;
  if (typeof toolName !== "string") return null;

  const subsystem = typeof record.subsystem === "string" ? record.subsystem : "";
  const reason = typeof record.reason === "string" ? record.reason : null;

  return {
    id: proposalId,
    sessionId,
    subsystem,
    toolName,
    toolInput: record.toolInput,
    reason,
    // broadcast เกิดตอนสร้าง proposal เท่านั้น จึงเป็น Pending เสมอ ไม่มี field status ส่งมาให้ derive
    status: "Pending",
    createdAt: now,
  };
}

/**
 * เติม proposal ใหม่ท้าย cache (list เดิมเรียงเก่า -> ใหม่) และ dedupe ด้วย id เพราะ proposal
 * เดียวกันอาจมาถึงทั้งจาก broadcast และจาก refetch ตอน initial fetch ยังไม่มี cache (undefined)
 * คืน undefined กลับไปตรง ๆ เพื่อให้ TanStack Query bail out จาก setQueryData — ปล่อยให้ fetch
 * ที่กำลังทำงานอยู่เป็น source of truth แทน
 */
export function mergeProposal(
  existing: Proposal[] | undefined,
  incoming: Proposal,
): Proposal[] | undefined {
  if (existing === undefined) return undefined;

  if (existing.some((p) => p.id === incoming.id)) return existing;

  return [...existing, incoming];
}

/**
 * ตัด proposal ที่ resolve แล้วออกจาก cache — คืน reference เดิมถ้าไม่เจอ id เพื่อกัน re-render เปล่า
 */
export function removeProposal(
  existing: Proposal[] | undefined,
  id: string,
): Proposal[] | undefined {
  if (existing === undefined) return undefined;

  if (!existing.some((p) => p.id === id)) return existing;

  return existing.filter((p) => p.id !== id);
}

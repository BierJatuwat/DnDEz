import { describe, it, expect } from "vitest";
import {
  normalizeProposalCreated,
  mergeProposal,
  removeProposal,
  type Proposal,
} from "./proposal-queue";

/**
 * ตรรกะ merge คิว proposal ถูกดึงออกมาจาก component เป็น plain TS ตามแบบเดียวกับ
 * campaign-hub-manager — เพราะมี branching จริง (payload จาก broadcast รูปร่างไม่เหมือน list item,
 * ต้อง dedupe, ต้องกัน payload เพี้ยน) ซึ่งไม่ควรฝังไว้ใน JSX ที่เทสต์ยาก
 */

const listItem = (over: Partial<Proposal> = {}): Proposal => ({
  id: "p-1",
  sessionId: "s-1",
  subsystem: "Dice",
  toolName: "roll_dice",
  toolInput: { expression: "1d20" },
  reason: "ต้องทอยเช็ค",
  status: "Pending",
  createdAt: "2026-08-27T10:00:00.000Z",
  ...over,
});

describe("normalizeProposalCreated", () => {
  it("maps the SignalR broadcast shape (proposalId) onto the list-item shape (id)", () => {
    const result = normalizeProposalCreated(
      {
        proposalId: "p-9",
        sessionId: "s-1",
        subsystem: "Dice",
        toolName: "roll_dice",
        toolInput: { expression: "2d6+3" },
        reason: "เช็คความเสียหาย",
      },
      "2026-08-27T11:00:00.000Z",
    );

    expect(result).toEqual({
      id: "p-9",
      sessionId: "s-1",
      subsystem: "Dice",
      toolName: "roll_dice",
      toolInput: { expression: "2d6+3" },
      reason: "เช็คความเสียหาย",
      // broadcast ไม่ได้ส่ง status/createdAt มาด้วย (ดู ProposalCreatedBroadcast) — broadcast เกิดขึ้น
      // ตอนสร้าง proposal เท่านั้น จึงเป็น Pending เสมอ ส่วนเวลาใช้เวลาที่ client รับได้
      status: "Pending",
      createdAt: "2026-08-27T11:00:00.000Z",
    });
  });

  it("keeps reason null when the broadcast omits it", () => {
    const result = normalizeProposalCreated({
      proposalId: "p-9",
      sessionId: "s-1",
      subsystem: "Dice",
      toolName: "roll_dice",
      toolInput: {},
    });

    expect(result?.reason).toBeNull();
  });

  it("preserves an arbitrary toolInput object as-is (tools อื่นในอนาคตไม่ได้มีแค่ expression)", () => {
    const result = normalizeProposalCreated({
      proposalId: "p-9",
      sessionId: "s-1",
      subsystem: "Combat",
      toolName: "apply_damage",
      toolInput: { characterId: "c-1", amount: 7, type: "slashing" },
    });

    expect(result?.toolInput).toEqual({
      characterId: "c-1",
      amount: 7,
      type: "slashing",
    });
  });

  it.each([
    ["null payload", null],
    ["a primitive payload", "boom"],
    ["missing proposalId", { sessionId: "s-1", toolName: "roll_dice" }],
    ["missing sessionId", { proposalId: "p-9", toolName: "roll_dice" }],
    ["non-string toolName", { proposalId: "p-9", sessionId: "s-1", toolName: 42 }],
  ])("returns null for %s instead of putting garbage in the cache", (_label, payload) => {
    expect(normalizeProposalCreated(payload)).toBeNull();
  });
});

describe("mergeProposal", () => {
  it("appends the new proposal at the end (list is oldest-first)", () => {
    const merged = mergeProposal([listItem({ id: "p-1" })], listItem({ id: "p-2" }));

    expect(merged?.map((p) => p.id)).toEqual(["p-1", "p-2"]);
  });

  it("dedupes by id so a proposal seen via broadcast AND via refetch appears once", () => {
    const existing = [listItem({ id: "p-1" }), listItem({ id: "p-2" })];

    const merged = mergeProposal(existing, listItem({ id: "p-2" }));

    expect(merged?.map((p) => p.id)).toEqual(["p-1", "p-2"]);
  });

  it("returns the identical array reference on duplicate so React skips a re-render", () => {
    const existing = [listItem({ id: "p-1" })];

    expect(mergeProposal(existing, listItem({ id: "p-1" }))).toBe(existing);
  });

  it("returns undefined when nothing is cached yet, so setQueryData bails out and the in-flight fetch stays the source of truth", () => {
    expect(mergeProposal(undefined, listItem())).toBeUndefined();
  });

  it("does not mutate the existing array", () => {
    const existing = [listItem({ id: "p-1" })];

    mergeProposal(existing, listItem({ id: "p-2" }));

    expect(existing).toHaveLength(1);
  });
});

describe("removeProposal", () => {
  it("drops the resolved proposal by id", () => {
    const existing = [listItem({ id: "p-1" }), listItem({ id: "p-2" })];

    expect(removeProposal(existing, "p-1")?.map((p) => p.id)).toEqual(["p-2"]);
  });

  it("returns the identical array reference when the id is not present", () => {
    const existing = [listItem({ id: "p-1" })];

    expect(removeProposal(existing, "p-nope")).toBe(existing);
  });

  it("returns undefined when nothing is cached", () => {
    expect(removeProposal(undefined, "p-1")).toBeUndefined();
  });
});

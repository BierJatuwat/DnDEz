import { describe, expect, it } from "vitest";
import { activityStatus, queriesToRefresh, type AssistantActivity } from "./assistant-view";

const act = (tool: string, status = "done"): AssistantActivity => ({
  toolUseId: `t-${tool}`,
  tool,
  summary: tool,
  status,
});

describe("activityStatus", () => {
  it("labels known statuses in Thai with a tone", () => {
    expect(activityStatus("done")).toEqual({ label: "ทำแล้ว", tone: "ok" });
    expect(activityStatus("error").tone).toBe("bad");
    expect(activityStatus("rejected").label).toBe("DM ปฏิเสธ");
  });

  it("falls back to the raw status for unknown values", () => {
    expect(activityStatus("something_new")).toEqual({ label: "something_new", tone: "muted" });
  });
});

describe("queriesToRefresh", () => {
  it("refreshes nothing for read-only tools", () => {
    expect(queriesToRefresh([act("lookup_rule"), act("get_game_state")])).toEqual([]);
  });

  it("refreshes only what a known tool changed", () => {
    expect(queriesToRefresh([act("write_journal_entry")])).toEqual(["journal"]);
  });

  it("ignores tools that did not actually run", () => {
    expect(queriesToRefresh([act("write_journal_entry", "rejected"), act("roll_dice", "error")])).toEqual([]);
  });

  it("refreshes a broad set for tools it does not know yet", () => {
    const keys = queriesToRefresh([act("spawn_monsters")]);
    expect(keys).toContain("characters");
    expect(keys).toContain("encounter-active");
    expect(keys).toContain("scene");
  });
});

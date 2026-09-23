import { describe, it, expect, beforeEach } from "vitest";
import { useConsoleStore } from "./console-store";

const initialState = useConsoleStore.getState();

describe("useConsoleStore", () => {
  beforeEach(() => {
    useConsoleStore.setState(initialState, true);
  });

  it("clears the selected session when a new campaign is selected", () => {
    useConsoleStore.getState().setSelectedSessionId("session-1");
    useConsoleStore.getState().setSelectedCampaignId("campaign-1");

    expect(useConsoleStore.getState().selectedSessionId).toBeNull();
    expect(useConsoleStore.getState().selectedCampaignId).toBe("campaign-1");
  });

  it("caps the log at 50 entries, newest first", () => {
    for (let i = 0; i < 55; i++) {
      useConsoleStore.getState().appendLog(`entry ${i}`);
    }

    const log = useConsoleStore.getState().log;
    expect(log).toHaveLength(50);
    expect(log[0].text).toBe("entry 54");
    expect(log[49].text).toBe("entry 5");
  });

  describe("action feed (Sprint E Part 3)", () => {
    const message = (id: string) => ({
      id,
      sessionId: "session-1",
      authorType: "Player",
      characterId: "character-1",
      content: `action ${id}`,
      createdAt: "2026-08-28T00:00:00Z",
    });

    it("appends action messages and dedupes by id", () => {
      useConsoleStore.getState().appendActionFeedMessage(message("m-1"));
      useConsoleStore.getState().appendActionFeedMessage(message("m-1"));
      useConsoleStore.getState().appendActionFeedMessage(message("m-2"));

      expect(useConsoleStore.getState().actionFeed.map((m) => m.id)).toEqual(["m-1", "m-2"]);
    });

    it("clears the action feed and the draft input when another session is selected", () => {
      useConsoleStore.getState().appendActionFeedMessage(message("m-1"));
      useConsoleStore.getState().setActionInput("ร่างที่ยังไม่ได้ส่ง");

      useConsoleStore.getState().setSelectedSessionId("session-2");

      expect(useConsoleStore.getState().actionFeed).toEqual([]);
      expect(useConsoleStore.getState().actionInput).toBe("");
    });

    it("clears the action feed when another campaign is selected", () => {
      useConsoleStore.getState().appendActionFeedMessage(message("m-1"));

      useConsoleStore.getState().setSelectedCampaignId("campaign-2");

      expect(useConsoleStore.getState().actionFeed).toEqual([]);
    });
  });
});

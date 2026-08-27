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
});

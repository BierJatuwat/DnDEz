import { create } from "zustand";

export type HubStatus = "disconnected" | "connecting" | "connected";
export type LogEntry = { at: string; text: string };

type ConsoleState = {
  selectedCampaignId: string | null;
  selectedSessionId: string | null;
  joinedCampaignId: string | null;
  hubStatus: HubStatus;
  log: LogEntry[];
  rollExpression: string;
  setSelectedCampaignId: (id: string | null) => void;
  setSelectedSessionId: (id: string | null) => void;
  setJoinedCampaignId: (id: string | null) => void;
  setHubStatus: (status: HubStatus) => void;
  setRollExpression: (value: string) => void;
  appendLog: (text: string) => void;
};

/** UI state ของ DM console (ไม่ใช่ server state) ตาม PROJECT-CONTEXT §3 —
 * Zustand สำหรับ UI state, TanStack Query สำหรับ server state (campaigns/sessions) */
export const useConsoleStore = create<ConsoleState>((set) => ({
  selectedCampaignId: null,
  selectedSessionId: null,
  joinedCampaignId: null,
  hubStatus: "disconnected",
  log: [],
  rollExpression: "1d20",
  setSelectedCampaignId: (id) =>
    set({ selectedCampaignId: id, selectedSessionId: null }),
  setSelectedSessionId: (id) => set({ selectedSessionId: id }),
  setJoinedCampaignId: (id) => set({ joinedCampaignId: id }),
  setHubStatus: (status) => set({ hubStatus: status }),
  setRollExpression: (value) => set({ rollExpression: value }),
  appendLog: (text) =>
    set((state) => ({
      log: [
        { at: new Date().toLocaleTimeString("th-TH"), text },
        ...state.log,
      ].slice(0, 50),
    })),
}));

import { create } from "zustand";

export type HubStatus = "disconnected" | "connecting" | "connected";
export type LogEntry = { at: string; text: string };

export type ConsultRole = "dm" | "ai" | "system";
export type ConsultMessage = { at: string; role: ConsultRole; text: string };

type ConsoleState = {
  selectedCampaignId: string | null;
  selectedSessionId: string | null;
  joinedCampaignId: string | null;
  hubStatus: HubStatus;
  log: LogEntry[];
  rollExpression: string;
  consultInput: string;
  consultMessages: ConsultMessage[];
  setSelectedCampaignId: (id: string | null) => void;
  setSelectedSessionId: (id: string | null) => void;
  setJoinedCampaignId: (id: string | null) => void;
  setHubStatus: (status: HubStatus) => void;
  setRollExpression: (value: string) => void;
  appendLog: (text: string) => void;
  setConsultInput: (value: string) => void;
  appendConsultMessage: (role: ConsultRole, text: string) => void;
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
  consultInput: "",
  consultMessages: [],
  setSelectedCampaignId: (id) =>
    set({
      selectedCampaignId: id,
      selectedSessionId: null,
      // แชทปรึกษา AI ผูกกับเซสชัน — เปลี่ยนแคมเปญคือเปลี่ยนเซสชันไปด้วยอยู่แล้ว ต้องล้างทิ้ง
      consultMessages: [],
      consultInput: "",
    }),
  setSelectedSessionId: (id) =>
    set({
      selectedSessionId: id,
      // แชทปรึกษา AI ผูกกับเซสชัน ห้ามให้ transcript ของเซสชันเก่าเล็ดลอดข้ามไป
      consultMessages: [],
      consultInput: "",
    }),
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
  setConsultInput: (value) => set({ consultInput: value }),
  appendConsultMessage: (role, text) =>
    set((state) => ({
      // ตั้งใจให้เก่าสุดอยู่บน ใหม่สุดอยู่ล่าง (ตรงข้าม appendLog) เพราะนี่คือบทสนทนา อ่านบนลงล่าง
      consultMessages: [
        ...state.consultMessages,
        { at: new Date().toLocaleTimeString("th-TH"), role, text },
      ].slice(-50),
    })),
}));

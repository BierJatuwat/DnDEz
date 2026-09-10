import { create } from "zustand";
import { appendActionMessage, type ActionMessage } from "./action-feed";
import type { ReactionPrompt } from "./reaction-prompt";

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
  actionInput: string;
  actionFeed: ActionMessage[];
  reactionPrompt: ReactionPrompt | null;
  setSelectedCampaignId: (id: string | null) => void;
  setSelectedSessionId: (id: string | null) => void;
  setJoinedCampaignId: (id: string | null) => void;
  setHubStatus: (status: HubStatus) => void;
  setRollExpression: (value: string) => void;
  appendLog: (text: string) => void;
  setConsultInput: (value: string) => void;
  appendConsultMessage: (role: ConsultRole, text: string) => void;
  setActionInput: (value: string) => void;
  appendActionFeedMessage: (message: ActionMessage) => void;
  /**
   * Sprint G Part 10 — prompt "จะสวนไหม" ที่มาจาก SignalR
   *
   * เก็บใน store ไม่ใช่ `useState` ของ component ให้เหมือน realtime state ตัวอื่นทั้งหมดในไฟล์นี้
   * (log/action feed/consult) — callback ของ hub ถูกผูกไว้ครั้งเดียวตอนสร้าง manager ค่าที่เขียนผ่าน
   * store จึงไม่ผูกกับ instance ไหนเป็นพิเศษ และรอดตอน component remount · ได้ cascade-clear ตอน
   * สลับเซสชัน/แคมเปญฟรีไปด้วย (prompt ของเซสชันเก่าต้องไม่ค้างข้ามเซสชัน)
   */
  setReactionPrompt: (prompt: ReactionPrompt | null) => void;
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
  actionInput: "",
  actionFeed: [],
  reactionPrompt: null,
  setSelectedCampaignId: (id) =>
    set({
      selectedCampaignId: id,
      selectedSessionId: null,
      // แชทปรึกษา AI + action feed ผูกกับเซสชัน — เปลี่ยนแคมเปญคือเปลี่ยนเซสชันไปด้วยอยู่แล้ว ต้องล้างทิ้ง
      consultMessages: [],
      consultInput: "",
      actionFeed: [],
      actionInput: "",
      reactionPrompt: null,
    }),
  setSelectedSessionId: (id) =>
    set({
      selectedSessionId: id,
      // แชทปรึกษา AI + action feed ผูกกับเซสชัน ห้ามให้ transcript ของเซสชันเก่าเล็ดลอดข้ามไป
      consultMessages: [],
      consultInput: "",
      actionFeed: [],
      actionInput: "",
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
  setActionInput: (value) => set({ actionInput: value }),
  setReactionPrompt: (prompt) => set({ reactionPrompt: prompt }),
  appendActionFeedMessage: (message) =>
    set((state) => ({ actionFeed: appendActionMessage(state.actionFeed, message) })),
}));

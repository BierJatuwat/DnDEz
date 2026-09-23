/**
 * ตรรกะแสดงผลของผู้ช่วย DM ช่องเดียว — แยกจาก component ให้เทสต์ได้ตรง ๆ
 */

export type AssistantActivity = {
  toolUseId: string;
  tool: string;
  summary: string;
  status: string;
};

export type PendingApproval = {
  toolUseId: string;
  tool: string;
  description: string;
  /** มีค่า = การ์ดนี้มีข้อความให้ DM แก้ก่อนอนุมัติ (ข้อความที่จะไปถึงผู้เล่น) */
  editableText: string | null;
};

export type AssistantTurn = {
  status: "completed" | "awaiting_approval" | string;
  runId: string;
  messageId: string | null;
  text: string;
  activity: AssistantActivity[];
  pending: PendingApproval[];
};

export type AssistantMessage = {
  id: string;
  role: "Dm" | "Ai" | string;
  content: string;
  sessionId: string | null;
  activity: AssistantActivity[];
  createdAt: string;
};

export type AssistantHistory = {
  messages: AssistantMessage[];
  pendingRun: AssistantTurn | null;
};

const STATUS_LABEL: Record<string, { label: string; tone: "ok" | "bad" | "muted" }> = {
  done: { label: "ทำแล้ว", tone: "ok" },
  error: { label: "ไม่สำเร็จ", tone: "bad" },
  rejected: { label: "DM ปฏิเสธ", tone: "muted" },
  cancelled: { label: "ยกเลิก", tone: "muted" },
};

export function activityStatus(status: string): { label: string; tone: "ok" | "bad" | "muted" } {
  return STATUS_LABEL[status] ?? { label: status, tone: "muted" };
}

/**
 * query ที่ต้องดึงใหม่หลังผู้ช่วยทำงาน — ดูจาก tool ที่ "ทำแล้ว" จริงเท่านั้น (ทอยเต๋าที่ถูกปฏิเสธไม่ได้เปลี่ยนอะไร)
 * · tool ที่ไม่รู้จัก (เพิ่มฝั่ง server ทีหลัง) ดึงชุดกว้างไว้ก่อน ดีกว่าจอค้างข้อมูลเก่า
 */
export function queriesToRefresh(activity: AssistantActivity[]): string[] {
  const keys = new Set<string>();
  for (const a of activity) {
    if (a.status !== "done") continue;
    switch (a.tool) {
      case "lookup_rule":
      case "lookup_monster":
      case "search_campaign_journal":
      case "get_game_state":
      case "search_srd_monsters":
      case "roll_dice":
        break;
      case "write_journal_entry":
      case "update_journal_entry":
        keys.add("journal");
        break;
      // ข้อความถึงผู้เล่นมาทาง broadcast `ActionMessageCreated` อยู่แล้ว (DM ก็อยู่ในกลุ่มแคมเปญ) ไม่ต้องดึงอะไรใหม่
      case "narrate_to_players":
      case "npc_say":
        break;
      case "generate_session_recap":
        keys.add("recap");
        keys.add("recap-latest");
        break;
      default:
        for (const k of ["characters", "encounter-active", "scene", "journal", "campaign-monsters", "recap"]) {
          keys.add(k);
        }
    }
  }
  return [...keys];
}

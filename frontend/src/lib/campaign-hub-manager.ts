export type HubStatus = "disconnected" | "connecting" | "connected";

/**
 * Sprint F งานที่ 6b — event ฝั่ง combat (encounter/turn/death save) ที่ backend broadcast ไว้ตั้งแต่
 * งานที่ 4-5 แต่ frontend ไม่เคยฟัง
 *
 * รวมเป็น callback เดียว (`onCombatEvent`) แทนที่จะแยกทีละตัวเหมือน event เดิมข้างบน **เพราะ
 * พฤติกรรมฝั่ง client เหมือนกันหมดจริง ๆ** คือ "invalidate query ที่เกี่ยวข้อง + log" — ต่างจาก
 * event เดิมที่แต่ละตัวทำคนละอย่าง (dice → log, proposal → merge cache, HP → patch cache,
 * action → ต่อท้าย feed) การแยก 6 ตัวที่ทำเหมือนกันเป๊ะจะได้แค่ boilerplate ไม่ได้ type safety เพิ่ม
 */
export const COMBAT_EVENT_NAMES = [
  "EncounterStarted",
  "TurnChanged",
  "TurnStateChanged",
  "EncounterEnded",
  "DeathSaveStateChanged",
  "DeathSaveRolled",
] as const;

export type CombatEventName = (typeof COMBAT_EVENT_NAMES)[number];

export type CampaignHubEvents = {
  onStatusChange: (status: HubStatus) => void;
  onJoinedChange: (campaignId: string | null) => void;
  onLog: (text: string) => void;
  onDiceRolled: (payload: unknown) => void;
  onProposalCreated: (payload: unknown) => void;
  onHpUpdated: (payload: unknown) => void;
  onActionMessageCreated: (payload: unknown) => void;
  onCombatEvent: (name: CombatEventName, payload: unknown) => void;
  /** Sprint G Part 6 — DM เปิด/ปิดพื้นที่หมอก แล้วทุก role ต้องดึง scene ใหม่ (สิ่งที่มองเห็นเปลี่ยน) */
  onFogChanged: (payload: unknown) => void;
  /**
   * Sprint H Part 6 — DM วาดพื้นเดินยาก/กำแพง แล้วทุก role ต้องดึง scene ใหม่ **และ** ขอบเขตเดินใหม่
   * · payload มีแค่ sceneId (พื้นในหมอกเป็นความลับ ผู้เล่นต้องได้เฉพาะส่วนที่ server กรองผ่าน GET scene)
   */
  onTerrainChanged: (payload: unknown) => void;
  /**
   * Sprint G Part 10 — มีคนเดินออกจากระยะเอื้อมของตัวที่เราคุม เรามีสิทธิ์สวนได้
   *
   * ⚠️ event นี้ยิงมาที่ **กลุ่มของผู้ใช้คนเดียว** (`campaign:{id}:user:{userId}`) ไม่ใช่ทั้งแคมเปญ —
   * ถ้ามาถึงเรา แปลว่าเรามีสิทธิ์จริง ไม่ต้องกรองซ้ำฝั่ง client
   */
  onReactionPrompt: (payload: unknown) => void;
  /** Sprint G Part 10 — การเดินที่ค้างอยู่เดินต่อแล้ว (มีคนตอบ หรือหมดเวลา) → ดึง scene ใหม่ */
  onMovementResumed: (payload: unknown) => void;
};

/**
 * Subset ของ @microsoft/signalr HubConnection ที่ manager ตัวนี้ใช้จริง — แยกเป็น interface ของ
 * ตัวเองแทนการ import type จาก signalR ตรง ๆ เพื่อให้เทสต์ inject fake connection ได้โดยไม่ต้อง
 * ผูกกับ transport จริง (WebSocket ไม่มีใน jsdom)
 */
export interface HubConnectionLike {
  on(methodName: string, callback: (...args: unknown[]) => void): void;
  onreconnecting(callback: (error?: Error) => void): void;
  onreconnected(callback: (connectionId?: string) => void): void;
  onclose(callback: (error?: Error) => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(methodName: string, ...args: unknown[]): Promise<unknown>;
}

/**
 * ดูแล lifecycle ของ SignalR connection + group membership ของแคมเปญที่เลือกอยู่ แยกออกจาก React
 * ตั้งใจให้ทดสอบได้อิสระด้วย fake HubConnectionLike (ดู campaign-hub-manager.test.ts)
 *
 * บั๊กที่เคยเกิดจริง (Sprint E Part 1 code review รอบ 1): @microsoft/signalr reconnect เปิด
 * ConnectionId ใหม่ แต่ server-side group membership (Groups.AddToGroupAsync) ผูกกับ ConnectionId
 * เดิม — ถ้าไม่ rejoin หลัง reconnect broadcast จะเงียบหายทั้งที่ status ยังโชว์ "connected"
 * (ดู `onreconnected` ด้านล่าง — ต้องเรียก syncGroup() เสมอ ไม่ใช่แค่เปลี่ยน status)
 *
 * `syncChain` serialize ทุกการ join/leave/rejoin ให้รันทีละอันเสมอ (กัน race ที่ operation สอง
 * ครั้งซ้อนกันยิง invoke() พร้อมกันตอนสลับแคมเปญเร็ว ๆ หรือ reconnect ระหว่างสลับแคมเปญ)
 */
export class CampaignHubManager {
  private joinedCampaignId: string | null = null;
  private selectedCampaignId: string | null = null;
  private stopped = false;
  /**
   * connection พร้อมรับ invoke() แล้วหรือยัง — Sprint F งานที่ 6a เพิ่มเข้ามาเพราะพอแยกหน้าเป็น
   * `/campaigns/{id}/...` แล้ว campaignId มาถึงตั้งแต่ mount (เดิมต้องรอผู้ใช้คลิกเลือกแคมเปญ ซึ่ง
   * ช้ากว่าการต่อ connection เสมอในทางปฏิบัติ) `setSelectedCampaign()` จึงถูกเรียกก่อน `start()`
   * จะ resolve ทำให้ยิง `invoke("JoinCampaign")` ใส่ connection ที่ยังไม่ connected แล้วขึ้น log
   * error ทุกครั้งที่เข้าหน้า (ตัวมันเอง recover ได้ เพราะ start() เรียก queueSync() ซ้ำ — แต่ผู้ใช้
   * เห็น error ที่ไม่ได้แปลว่าอะไรพังจริง) · กันด้วยการไม่ sync จนกว่าจะ connected แล้วจริง
   */
  private started = false;
  private syncChain: Promise<void> = Promise.resolve();

  constructor(
    private readonly connection: HubConnectionLike,
    private readonly events: CampaignHubEvents,
  ) {
    connection.on("DiceRolled", (payload) => this.events.onDiceRolled(payload));
    connection.on("ReactionPrompt", (payload) => this.events.onReactionPrompt(payload));
    connection.on("MovementResumed", (payload) => this.events.onMovementResumed(payload));
    connection.on("ProposalCreated", (payload) => this.events.onProposalCreated(payload));
    // Sprint E Part 3 — HP realtime (backend broadcast มีอยู่แล้วตั้งแต่ Sprint C Part 3, ฝั่งนี้
    // แค่ไม่เคยฟังมาก่อน) + player action feed ใหม่ (ดู OrchestratorTurnRunner ฝั่ง API)
    connection.on("HpUpdated", (payload) => this.events.onHpUpdated(payload));
    connection.on("ActionMessageCreated", (payload) => this.events.onActionMessageCreated(payload));

    // Sprint F งานที่ 6b — encounter/turn/death save (backend broadcast มาตั้งแต่งานที่ 4-5)
    for (const name of COMBAT_EVENT_NAMES) {
      connection.on(name, (payload) => this.events.onCombatEvent(name, payload));
    }

    // Sprint G Part 6 — server ยิงเข้า role-group แยก (dm/player) แต่ฝั่ง client ไม่ต้องรู้ว่าตัวเอง
    // อยู่ group ไหน: ได้รับเมื่อไหร่แปลว่า "สิ่งที่เรามองเห็นเปลี่ยนแล้ว" ให้ดึง scene ใหม่เท่านั้น
    connection.on("FogChanged", (payload) => this.events.onFogChanged(payload));

    // Sprint H Part 6 — คนละความหมายกับหมอก: พื้นเปลี่ยน "เดินได้ถึงไหน" ด้วย (ต้อง invalidate reachable)
    connection.on("TerrainChanged", (payload) => this.events.onTerrainChanged(payload));

    connection.onreconnecting(() => {
      this.started = false;
      this.joinedCampaignId = null;
      this.events.onJoinedChange(null);
      this.events.onStatusChange("connecting");
    });

    connection.onreconnected(() => {
      this.started = true;
      this.events.onStatusChange("connected");
      this.queueSync();
    });

    connection.onclose(() => {
      this.started = false;
      this.joinedCampaignId = null;
      this.events.onJoinedChange(null);
      this.events.onStatusChange("disconnected");
    });
  }

  async start(): Promise<void> {
    this.events.onStatusChange("connecting");
    try {
      await this.connection.start();
    } catch (err) {
      if (this.stopped) return;
      this.events.onStatusChange("disconnected");
      this.events.onLog(`เชื่อมต่อ SignalR ไม่สำเร็จ: ${String(err)}`);
      return;
    }

    if (this.stopped) return;
    this.started = true;
    this.events.onStatusChange("connected");
    this.events.onLog("เชื่อมต่อ SignalR hub สำเร็จ");
    // sync ที่นี่ครอบเคส "เลือกแคมเปญไว้ก่อน connection พร้อม" ด้วย — doSync อ่าน
    // selectedCampaignId สดตอนรัน ไม่ใช่ค่าที่ capture ไว้ตอน setSelectedCampaign()
    this.queueSync();
  }

  async stop(): Promise<void> {
    this.stopped = true;
    await this.connection.stop();
  }

  setSelectedCampaign(campaignId: string | null): void {
    this.selectedCampaignId = campaignId;
    this.queueSync();
  }

  private queueSync(): void {
    // ยังไม่ connected = ยังยิง invoke ไม่ได้ · ไม่ต้องเก็บคิวไว้เอง เพราะ start()/onreconnected()
    // เรียก queueSync() ต่อทันทีที่พร้อม แล้ว doSync อ่าน selectedCampaignId ล่าสุดเสมอ
    if (!this.started) return;
    this.syncChain = this.syncChain.then(() => this.doSync());
  }

  private async doSync(): Promise<void> {
    if (this.stopped) return;
    const target = this.selectedCampaignId;
    if (target === this.joinedCampaignId) return;

    if (this.joinedCampaignId) {
      await this.connection.invoke("LeaveCampaign", this.joinedCampaignId);
    }

    if (!target) {
      this.joinedCampaignId = null;
      this.events.onJoinedChange(null);
      return;
    }

    try {
      await this.connection.invoke("JoinCampaign", target);
      this.joinedCampaignId = target;
      this.events.onJoinedChange(target);
      this.events.onLog(`join group ของแคมเปญ ${target} สำเร็จ`);
    } catch (err) {
      this.events.onLog(`join group ไม่สำเร็จ: ${String(err)}`);
    }
  }
}

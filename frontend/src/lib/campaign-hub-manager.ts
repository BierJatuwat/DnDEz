export type HubStatus = "disconnected" | "connecting" | "connected";

export type CampaignHubEvents = {
  onStatusChange: (status: HubStatus) => void;
  onJoinedChange: (campaignId: string | null) => void;
  onLog: (text: string) => void;
  onDiceRolled: (payload: unknown) => void;
  onProposalCreated: (payload: unknown) => void;
  onHpUpdated: (payload: unknown) => void;
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
  private syncChain: Promise<void> = Promise.resolve();

  constructor(
    private readonly connection: HubConnectionLike,
    private readonly events: CampaignHubEvents,
  ) {
    connection.on("DiceRolled", (payload) => this.events.onDiceRolled(payload));
    connection.on("ProposalCreated", (payload) => this.events.onProposalCreated(payload));
    connection.on("HpUpdated", (payload) => this.events.onHpUpdated(payload));

    connection.onreconnecting(() => {
      this.joinedCampaignId = null;
      this.events.onJoinedChange(null);
      this.events.onStatusChange("connecting");
    });

    connection.onreconnected(() => {
      this.events.onStatusChange("connected");
      this.queueSync();
    });

    connection.onclose(() => {
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
    this.events.onStatusChange("connected");
    this.events.onLog("เชื่อมต่อ SignalR hub สำเร็จ");
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

import { describe, it, expect, vi, beforeEach } from "vitest";
import { CampaignHubManager, type HubConnectionLike } from "./campaign-hub-manager";

/**
 * Fake ที่ implement เฉพาะส่วนของ HubConnection ที่ manager ใช้จริง — เก็บ callback ที่ manager
 * ลงทะเบียนไว้ใน registry ให้เทสต์เรียก trigger เองได้ (จำลอง event จาก server/transport จริง
 * โดยไม่ต้องมี WebSocket จริง)
 */
class FakeHubConnection implements HubConnectionLike {
  handlers: Record<string, ((...args: unknown[]) => void)[]> = {};
  reconnectingCb: ((error?: Error) => void) | null = null;
  reconnectedCb: ((connectionId?: string) => void) | null = null;
  closeCb: ((error?: Error) => void) | null = null;

  start = vi.fn().mockResolvedValue(undefined);
  stop = vi.fn().mockResolvedValue(undefined);
  invoke = vi.fn().mockResolvedValue(undefined);

  on(methodName: string, callback: (...args: unknown[]) => void): void {
    (this.handlers[methodName] ??= []).push(callback);
  }

  onreconnecting(callback: (error?: Error) => void): void {
    this.reconnectingCb = callback;
  }

  onreconnected(callback: (connectionId?: string) => void): void {
    this.reconnectedCb = callback;
  }

  onclose(callback: (error?: Error) => void): void {
    this.closeCb = callback;
  }

  emit(methodName: string, payload: unknown): void {
    for (const cb of this.handlers[methodName] ?? []) cb(payload);
  }
}

function makeEvents() {
  return {
    onStatusChange: vi.fn(),
    onJoinedChange: vi.fn(),
    onLog: vi.fn(),
    onDiceRolled: vi.fn(),
    onProposalCreated: vi.fn(),
    onHpUpdated: vi.fn(),
    onActionMessageCreated: vi.fn(),
  };
}

// helper ให้ promise microtask queue (ที่ manager ใช้ chain การ join/leave) ได้รันจบก่อน assert
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("CampaignHubManager", () => {
  let connection: FakeHubConnection;
  let events: ReturnType<typeof makeEvents>;
  let manager: CampaignHubManager;

  beforeEach(() => {
    connection = new FakeHubConnection();
    events = makeEvents();
    manager = new CampaignHubManager(connection, events);
  });

  it("start() connects and reports connected status", async () => {
    await manager.start();
    await flush();

    expect(connection.start).toHaveBeenCalledOnce();
    expect(events.onStatusChange).toHaveBeenCalledWith("connecting");
    expect(events.onStatusChange).toHaveBeenCalledWith("connected");
  });

  it("joins the selected campaign once connected", async () => {
    await manager.start();
    manager.setSelectedCampaign("campaign-1");
    await flush();

    expect(connection.invoke).toHaveBeenCalledWith("JoinCampaign", "campaign-1");
    expect(events.onJoinedChange).toHaveBeenCalledWith("campaign-1");
  });

  it("leaves the old group and joins the new one when the selected campaign changes", async () => {
    await manager.start();
    manager.setSelectedCampaign("campaign-1");
    await flush();
    connection.invoke.mockClear();

    manager.setSelectedCampaign("campaign-2");
    await flush();

    expect(connection.invoke).toHaveBeenNthCalledWith(1, "LeaveCampaign", "campaign-1");
    expect(connection.invoke).toHaveBeenNthCalledWith(2, "JoinCampaign", "campaign-2");
  });

  it("forwards DiceRolled events to onDiceRolled", async () => {
    await manager.start();
    connection.emit("DiceRolled", { total: 9, expression: "1d20" });

    expect(events.onDiceRolled).toHaveBeenCalledWith({ total: 9, expression: "1d20" });
  });

  it("forwards ProposalCreated events to onProposalCreated", async () => {
    await manager.start();
    const payload = {
      proposalId: "p-1",
      sessionId: "s-1",
      subsystem: "Dice",
      toolName: "roll_dice",
      toolInput: { expression: "1d20" },
      reason: "ต้องทอยเช็ค",
    };

    connection.emit("ProposalCreated", payload);

    expect(events.onProposalCreated).toHaveBeenCalledWith(payload);
    // คนละ handler กัน — ProposalCreated ต้องไม่ไปโผล่ที่ onDiceRolled
    expect(events.onDiceRolled).not.toHaveBeenCalled();
  });

  it("forwards HpUpdated events to onHpUpdated (Sprint E Part 3 — backend broadcast already existed since Sprint C Part 3, this is the first listener)", async () => {
    await manager.start();
    const payload = { characterId: "c-1", newHp: 7, previousHp: 12 };

    connection.emit("HpUpdated", payload);

    expect(events.onHpUpdated).toHaveBeenCalledWith(payload);
    expect(events.onDiceRolled).not.toHaveBeenCalled();
  });

  it("forwards ActionMessageCreated events to onActionMessageCreated", async () => {
    await manager.start();
    const payload = {
      messageId: "m-1",
      sessionId: "s-1",
      authorType: "Player",
      characterId: "c-1",
      content: "I search the room for traps.",
      createdAt: "2026-08-28T00:00:00Z",
    };

    connection.emit("ActionMessageCreated", payload);

    expect(events.onActionMessageCreated).toHaveBeenCalledWith(payload);
    expect(events.onProposalCreated).not.toHaveBeenCalled();
  });

  it(
    "REGRESSION (code review Sprint E Part 1 round 1): rejoins the campaign group after a " +
      "reconnect, since @microsoft/signalr issues a new ConnectionId that the server no longer " +
      "has in the group — without this, broadcasts silently stop while status still shows connected",
    async () => {
      await manager.start();
      manager.setSelectedCampaign("campaign-1");
      await flush();
      expect(connection.invoke).toHaveBeenCalledWith("JoinCampaign", "campaign-1");
      connection.invoke.mockClear();

      // จำลอง network blip: SignalR เรียก onreconnecting แล้วต่อมา onreconnected เอง
      connection.reconnectingCb?.();
      expect(events.onJoinedChange).toHaveBeenCalledWith(null);
      expect(events.onStatusChange).toHaveBeenCalledWith("connecting");

      connection.reconnectedCb?.();
      await flush();

      expect(events.onStatusChange).toHaveBeenCalledWith("connected");
      // นี่คือจุดที่บั๊กเดิมพลาด — ต้อง JoinCampaign ซ้ำด้วย ConnectionId ใหม่ ไม่ใช่แค่เปลี่ยน status
      expect(connection.invoke).toHaveBeenCalledWith("JoinCampaign", "campaign-1");
      expect(events.onJoinedChange).toHaveBeenCalledWith("campaign-1");
    },
  );

  it("clears joined state on close without leaving the (already-gone) connection", async () => {
    await manager.start();
    manager.setSelectedCampaign("campaign-1");
    await flush();

    connection.closeCb?.();

    expect(events.onJoinedChange).toHaveBeenCalledWith(null);
    expect(events.onStatusChange).toHaveBeenCalledWith("disconnected");
  });

  it("does not report connected/log success if stop() is called before start() resolves (StrictMode double-invoke)", async () => {
    let resolveStart!: () => void;
    connection.start.mockReturnValue(
      new Promise<void>((resolve) => {
        resolveStart = resolve;
      }),
    );

    const startPromise = manager.start();
    await manager.stop();
    resolveStart();
    await startPromise;

    expect(events.onStatusChange).not.toHaveBeenCalledWith("connected");
  });
});

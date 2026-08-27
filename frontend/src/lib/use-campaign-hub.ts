"use client";

import { useEffect, useRef } from "react";
import { createCampaignHubConnection } from "./signalr";
import { CampaignHubManager, type CampaignHubEvents } from "./campaign-hub-manager";

type UseCampaignHubOptions = CampaignHubEvents & {
  token: string | null;
  selectedCampaignId: string | null;
};

/** React glue บาง ๆ รอบ CampaignHubManager — ตรรกะจริง (rejoin หลัง reconnect ฯลฯ) อยู่ใน
 * campaign-hub-manager.ts ทั้งหมด เพื่อให้เทสต์ได้โดยไม่ต้องเรนเดอร์ React */
export function useCampaignHub({
  token,
  selectedCampaignId,
  onStatusChange,
  onJoinedChange,
  onLog,
  onDiceRolled,
}: UseCampaignHubOptions) {
  const managerRef = useRef<CampaignHubManager | null>(null);

  useEffect(() => {
    if (!token) return;

    const connection = createCampaignHubConnection(() => token);
    const manager = new CampaignHubManager(connection, {
      onStatusChange,
      onJoinedChange,
      onLog,
      onDiceRolled,
    });
    managerRef.current = manager;
    void manager.start();

    return () => {
      void manager.stop();
      managerRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- onStatusChange/onJoinedChange/onLog/onDiceRolled เป็น zustand action/closure อ้างอิงคงที่พอในทางปฏิบัติ ไม่ต้องใส่
  }, [token]);

  useEffect(() => {
    managerRef.current?.setSelectedCampaign(selectedCampaignId);
  }, [selectedCampaignId]);
}

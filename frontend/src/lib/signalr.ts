import * as signalR from "@microsoft/signalr";
import { API_BASE_URL } from "./api";

/**
 * Sprint F defect A3 — @microsoft/signalr log ข้อความนี้เป็น LogLevel.Error เสมอไม่ว่าสาเหตุจะ
 * เป็นอะไร (ดู HttpConnection.js: `this._logger.log(LogLevel.Error, "Failed to start the
 * connection: " + e)`) รวมถึงเคสที่เรา *ตั้งใจ* เรียก stop() ระหว่างที่ start() ยังไม่ resolve —
 * เกิดขึ้นจริงตอน React effect cleanup รันซ้ำ (dev mode) หรือสลับหน้าเร็ว ๆ ก่อน connection
 * แรกต่อเสร็จ CampaignHubManager.start() ดักเคสนี้ไว้แล้วด้วย `this.stopped` (ไม่ log ต่อผู้ใช้
 * เห็นเอง) แต่ library เองพ่น console.error ไปก่อนที่ catch ของเราจะได้ทำงานด้วยซ้ำ ทำให้ดูเหมือน
 * แอปพัง ทั้งที่ connection ตัวจริงต่อสำเร็จเสมอ (ยืนยันจาก log ถัดมาที่ขึ้น "WebSocket connected")
 *
 * กรองเฉพาะ error message รูปแบบนี้ทิ้ง — ไม่ใช้ configureLogging(LogLevel.None) เพราะจะกลบ
 * error จริงอื่น ๆ ไปด้วย ที่เหลือ log เหมือน ConsoleLogger(Information) ที่เป็นค่าเริ่มต้นเดิม
 */
class FilteredSignalRLogger implements signalR.ILogger {
  private static readonly BENIGN_ABORT_PATTERN = /stopped during (negotiation|start)/i;

  log(logLevel: signalR.LogLevel, message: string): void {
    if (
      logLevel === signalR.LogLevel.Error &&
      FilteredSignalRLogger.BENIGN_ABORT_PATTERN.test(message)
    ) {
      return;
    }
    if (logLevel < signalR.LogLevel.Information) return;

    const formatted = `[${new Date().toISOString()}] ${signalR.LogLevel[logLevel]}: ${message}`;
    if (logLevel >= signalR.LogLevel.Error) console.error(formatted);
    else if (logLevel === signalR.LogLevel.Warning) console.warn(formatted);
    else console.info(formatted);
  }
}

/**
 * ต้องตรงกับ CampaignHubPath ใน Program.cs เป๊ะ ("/hubs/campaign") — backend รับ JWT ผ่าน
 * query string (`?access_token=`) เฉพาะ path นี้เท่านั้น เพราะ browser WebSocket ตั้ง custom
 * header ตอน handshake เองไม่ได้ (ดู PROJECT-CONTEXT §4 Sprint C Part 1)
 *
 * withCredentials: false เพราะ auth ทั้งระบบใช้ Bearer token ไม่ใช่ cookie — ไม่ต้องการให้
 * client ส่ง cookie ข้าม origin ไปหา API โดยไม่จำเป็น (backend ก็ไม่เปิด AllowCredentials ด้วย)
 */
export function createCampaignHubConnection(getToken: () => string | null) {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE_URL}/hubs/campaign`, {
      accessTokenFactory: () => getToken() ?? "",
      withCredentials: false,
    })
    .withAutomaticReconnect()
    .configureLogging(new FilteredSignalRLogger())
    .build();
}

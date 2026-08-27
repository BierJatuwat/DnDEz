import * as signalR from "@microsoft/signalr";
import { API_BASE_URL } from "./api";

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
    .build();
}

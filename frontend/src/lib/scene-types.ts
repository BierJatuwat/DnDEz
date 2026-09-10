/**
 * Sprint G Part 6 — type ของ scene ที่ API ส่งกลับมา
 *
 * ⚠️ ต้องตรงกับ `SceneResponse` / `TokenResponse` / `FogAreaResponse` ฝั่ง C# เป๊ะ
 * (`src/DnDEz.Api/Endpoints/SceneEndpoints.cs`) — ไม่ได้ generate อัตโนมัติ แก้ฝั่งไหนต้องมาแก้ที่นี่ด้วย
 *
 * ⚠️⚠️ **payload นี้ถูกกรองมาแล้วจาก server ตาม role ของ token ที่ยิงไป** (design doc §13.3) —
 * ผู้เล่นจะไม่ได้ token ที่อยู่นอกพื้นที่ที่เปิดแล้วมาเลย (ไม่ใช่ได้มาแล้วให้ client ซ่อน) และ
 * `currentHp/maxHp` เป็น `null` แปลว่า "server ตั้งใจไม่บอกเรา" ไม่ใช่ "ยังไม่มีข้อมูล"
 * ห้ามเขียนโค้ดฝั่ง client ที่พยายามเติมค่าที่หายไปตรงนี้จากที่อื่น
 */

export type SceneToken = {
  id: string;
  entityType: "Character" | "Monster" | string;
  entityId: string;
  x: number;
  y: number;
  /** null = NPC ที่ DM ปิดการมองเห็นไว้ และเราไม่ใช่ DM */
  currentHp: number | null;
  maxHp: number | null;
};

/** พื้นที่ที่ DM เปิดให้เห็นแล้วหนึ่งวง — นอกวงเหล่านี้คือพื้นที่ที่ยังไม่ได้สำรวจ */
export type FogArea = {
  id: string;
  x: number;
  y: number;
  radius: number;
};

export type Scene = {
  id: string;
  sessionId: string;
  name: string;
  /** jsonb ดิบ — `{"pixelsPerSquare":..,"offsetX":..,"offsetY":..}` (Sprint G Part 4) */
  grid: string;
  mapAssetId: string | null;
  /** signed URL อายุสั้น — generate ใหม่ทุกครั้งที่ GET ห้าม cache ยาว */
  imageUrl: string | null;
  isActive: boolean;
  tokens: SceneToken[];
  revealedAreas: FogArea[];
};

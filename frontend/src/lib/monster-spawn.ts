/** ต้องตรงกับ `MonsterSpawnEndpoints.MaxSpawnCount` ฝั่ง C# */
export const MAX_SPAWN_COUNT = 20;

/** จำนวนตัวที่จะ spawn จากช่องกรอก — null = กรอกไม่ถูก (ปุ่มต้องกดไม่ได้ ไม่ใช่ปัดค่าให้เงียบ ๆ) */
export function parseSpawnCount(raw: string): number | null {
  const trimmed = raw.trim();
  if (!/^\d+$/.test(trimmed)) return null;
  const n = Number(trimmed);
  return n >= 1 && n <= MAX_SPAWN_COUNT ? n : null;
}

/** "Goblin #1" → "#1" · หลายตัว → "#1–#3" ใช้บอก DM ว่าเพิ่งส่งตัวไหนลงสนาม */
export function describeSpawned(names: string[]): string {
  if (names.length === 0) return "";
  if (names.length === 1) return names[0];
  const last = names[names.length - 1];
  const suffix = last.match(/#\d+$/)?.[0];
  return suffix ? `${names[0]} – ${suffix}` : `${names[0]} และอีก ${names.length - 1} ตัว`;
}

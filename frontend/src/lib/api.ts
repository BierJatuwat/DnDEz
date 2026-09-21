export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5080";

export class ApiError extends Error {
  constructor(
    public status: number,
    public body: unknown,
  ) {
    super(`API request failed with status ${status}`);
  }
}

/**
 * ถูกเรียกเมื่อ request ที่ **แนบ token ไปแล้ว** ได้ 401 กลับมา = token หมดอายุหรือใช้ไม่ได้แล้ว
 *
 * **ทำไมต้องมี** (2026-09-21): อายุ token คือ 1 ชั่วโมงและยังไม่มี refresh token (backlog ตั้งแต่ Sprint A
 * Part 2) แต่ token ถูก persist ไว้ใน localStorage — พอหมดอายุ หน้าจอยังคิดว่า "ล็อกอินอยู่" ทุกคำขอจึงได้ 401
 * เงียบ ๆ แล้วหน้าแรกไปขึ้นว่า **"ยังไม่ได้อยู่โต๊ะไหนเลย"** ซึ่งเป็นคำตอบที่ผิดความจริง (แคมเปญยังอยู่ครบ
 * แค่เราไม่ได้ล็อกอินแล้ว) — ผู้ใช้เจอจริงแล้วเข้าใจว่าแคมเปญหาย
 *
 * ลงทะเบียน handler ที่ `auth-store.ts` (ไม่ import store มาที่นี่ เพราะ store เป็นฝ่าย import `apiFetch`
 * อยู่แล้ว — import กลับจะเป็นวงกลม)
 */
type UnauthorizedHandler = () => void;

let unauthorizedHandler: UnauthorizedHandler | null = null;

export function setUnauthorizedHandler(handler: UnauthorizedHandler | null) {
  unauthorizedHandler = handler;
}

type ApiFetchOptions = {
  // PATCH เพิ่มเข้ามาตอน Sprint H Part 2 — ฝั่ง API มี endpoint ที่เป็น PATCH อยู่ 4 ตัวตั้งแต่
  // Sprint G (grid, token, beat, beat order) แต่ไม่เคยมีโค้ดฝั่ง frontend เรียกเลยจนถึงรอบนี้
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  token?: string | null;
};

export async function apiFetch<T>(
  path: string,
  { method = "GET", body, token }: ApiFetchOptions = {},
): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  if (token) {
    headers["Authorization"] = `Bearer ${token}`;
  }

  const response = await fetch(`${API_BASE_URL}${path}`, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  const text = await response.text();
  const data = text ? JSON.parse(text) : null;

  if (!response.ok) {
    // เฉพาะคำขอที่แนบ token ไปแล้วเท่านั้น — 401 ของ `/auth/login` แปลว่า "รหัสผ่านผิด" ไม่ใช่ "หมดอายุ"
    // การเคลียร์ session ตรงนั้นไม่ผิด (ยังไม่ได้ล็อกอินอยู่แล้ว) แต่จะทำให้ handler ถูกเรียกในจังหวะที่
    // ไม่มีอะไรให้เคลียร์ ซึ่งอ่านโค้ดแล้วชวนสับสนว่าเจตนาคืออะไร
    if (response.status === 401 && token) {
      unauthorizedHandler?.();
    }

    throw new ApiError(response.status, data);
  }

  return data as T;
}

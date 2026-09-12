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
    throw new ApiError(response.status, data);
  }

  return data as T;
}

import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { apiFetch, ApiError, setUnauthorizedHandler } from "./api";

/**
 * พฤติกรรมตอน token หมดอายุ — เคสที่ผู้ใช้เจอจริง 2026-09-21: อายุ token คือ 1 ชั่วโมง ไม่มี refresh token
 * และ token ถูก persist ใน localStorage · พอหมดอายุ ทุกคำขอได้ 401 แต่หน้าจอยังคิดว่าล็อกอินอยู่
 * แล้วหน้าแรกขึ้นว่า "ยังไม่ได้อยู่โต๊ะไหนเลย" ทั้งที่แคมเปญยังอยู่ครบในฐานข้อมูล
 */

function jsonResponse(status: number, body: unknown) {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(JSON.stringify(body)),
  } as Response;
}

describe("apiFetch — 401", () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    vi.stubGlobal("fetch", fetchMock);
    fetchMock.mockReset();
  });

  afterEach(() => {
    setUnauthorizedHandler(null);
    vi.unstubAllGlobals();
  });

  it("เรียก handler เมื่อคำขอที่แนบ token ได้ 401 (= token หมดอายุ ต้องจบ session)", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    fetchMock.mockResolvedValue(jsonResponse(401, { error: "unauthorized" }));

    await expect(apiFetch("/campaigns", { token: "expired-token" })).rejects.toBeInstanceOf(ApiError);
    expect(handler).toHaveBeenCalledOnce();
  });

  it("**ไม่** เรียก handler เมื่อคำขอที่ไม่มี token ได้ 401 (เช่นล็อกอินด้วยรหัสผ่านผิด)", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    fetchMock.mockResolvedValue(jsonResponse(401, { error: "invalid_credentials" }));

    await expect(apiFetch("/auth/login", { method: "POST", body: {} })).rejects.toBeInstanceOf(ApiError);
    expect(handler).not.toHaveBeenCalled();
  });

  it("ไม่เรียก handler กับ error อื่น — 403/404/500 ไม่ได้แปลว่า session หมดอายุ", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);

    for (const status of [403, 404, 500]) {
      fetchMock.mockResolvedValue(jsonResponse(status, { error: "nope" }));
      await expect(apiFetch("/campaigns", { token: "t" })).rejects.toBeInstanceOf(ApiError);
    }

    expect(handler).not.toHaveBeenCalled();
  });

  it("ยัง throw ApiError ที่มี status เดิมเสมอ — ปลายทางต้องแยกเคสเองได้ ไม่ใช่ถูกกลืนไปกับการ logout", async () => {
    setUnauthorizedHandler(vi.fn());
    fetchMock.mockResolvedValue(jsonResponse(401, { error: "unauthorized" }));

    const error = await apiFetch("/campaigns", { token: "t" }).catch((e) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(401);
  });

  it("ไม่มี handler ลงทะเบียนไว้ก็ต้องไม่พัง", async () => {
    setUnauthorizedHandler(null);
    fetchMock.mockResolvedValue(jsonResponse(401, { error: "unauthorized" }));

    await expect(apiFetch("/campaigns", { token: "t" })).rejects.toBeInstanceOf(ApiError);
  });

  it("คำขอที่สำเร็จไม่แตะ handler เลย", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    fetchMock.mockResolvedValue(jsonResponse(200, [{ id: "c-1" }]));

    await expect(apiFetch("/campaigns", { token: "t" })).resolves.toEqual([{ id: "c-1" }]);
    expect(handler).not.toHaveBeenCalled();
  });
});

import { ApiError } from "./api";

/**
 * ข้อความภาษาคนสำหรับ error ของปุ่มที่ให้ AI สรุป (recap / บันทึกแคมเปญ) — server ส่งรหัสมาใน
 * `{ error: "..." }` ของ 400 · รหัสที่ไม่รู้จักตกไปข้อความกลาง ไม่ใช่โชว์รหัสดิบให้ DM เดา
 */
export function describeSummaryError(err: unknown): string {
  if (err instanceof ApiError) {
    const code =
      typeof err.body === "object" && err.body !== null && "error" in err.body
        ? String((err.body as { error: unknown }).error)
        : null;
    if (code === "dm_api_key_not_set") return "ยังไม่ได้ตั้ง Anthropic API key — ตั้งได้ที่หัวข้อ \"Anthropic API key ของฉัน\" คอลัมน์ซ้าย";
    if (code === "session_has_no_messages") return "เซสชันนี้ยังไม่มีบทสนทนาให้สรุป";
    if (code === "session_not_in_campaign") return "เซสชันนี้ไม่ได้อยู่ในแคมเปญนี้";
    if (err.status === 403) return "เฉพาะ DM เจ้าของแคมเปญเท่านั้น";
  }
  return "ทำไม่สำเร็จ ลองใหม่อีกครั้ง";
}

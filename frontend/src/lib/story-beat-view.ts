import { ApiError } from "./api";

/**
 * เนื้อเรื่อง (story beats) ฝั่ง DM — ตรรกะล้วนแยกจาก React ให้เทสต์ได้ตรง ๆ
 *
 * `secrets` ฝั่ง server เป็น jsonb `[{ "text": "...", "reveal_condition": "..." }]` ที่ถูกส่งเข้า prompt ของผู้ช่วย DM
 * ตรง ๆ และ server ปฏิเสธ (400 `invalid_secrets`) ถ้าไม่ใช่ array ของ object ที่มี `text` — ฟอร์มจึงแก้เป็นรายการ
 * แล้วแปลงกลับด้วย `serializeSecrets` เสมอ ไม่ให้ DM พิมพ์ JSON เอง
 */

export type BeatProgress = "Upcoming" | "Active" | "Passed";
export type BeatReviewStatus = "Pending" | "Approved" | "Rejected";

export type StoryBeat = {
  id: string;
  order: number;
  title: string;
  summary: string | null;
  objective: string | null;
  entryTrigger: string | null;
  exitConditions: string[];
  dmNotes: string | null;
  secrets: string;
  progress: BeatProgress | string;
  reviewStatus: BeatReviewStatus | string;
};

export type BeatSecret = { text: string; revealCondition: string };

export function parseSecrets(json: string | null | undefined): BeatSecret[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    if (!Array.isArray(parsed)) return [];
    return parsed.flatMap((item) => {
      if (typeof item !== "object" || item === null) return [];
      const { text, reveal_condition } = item as Record<string, unknown>;
      if (typeof text !== "string") return [];
      return [{ text, revealCondition: typeof reveal_condition === "string" ? reveal_condition : "" }];
    });
  } catch {
    return [];
  }
}

export function serializeSecrets(secrets: BeatSecret[]): string {
  return JSON.stringify(
    secrets
      .map((s) => ({ text: s.text.trim(), revealCondition: s.revealCondition.trim() }))
      .filter((s) => s.text)
      .map((s) => (s.revealCondition ? { text: s.text, reveal_condition: s.revealCondition } : { text: s.text })),
  );
}

/** เงื่อนไขจบฉาก — ฟอร์มใช้หนึ่งบรรทัดต่อหนึ่งเงื่อนไข */
export function splitLines(text: string): string[] {
  return text
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

/** ลำดับ id ใหม่หลังเลื่อนฉากขึ้น/ลงหนึ่งช่อง — null = เลื่อนไม่ได้ (อยู่ขอบแล้ว/ไม่มี id นี้) */
export function moveBeat(ids: string[], id: string, delta: -1 | 1): string[] | null {
  const index = ids.indexOf(id);
  const target = index + delta;
  if (index < 0 || target < 0 || target >= ids.length) return null;
  const next = [...ids];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

/** ตั้งเป็นฉากปัจจุบันได้ไหม — ฉากที่ยังไม่ผ่านการตรวจ AI มองไม่เห็นอยู่ดี (prompt อ่านแต่ Approved) */
export function activationBlocker(beat: StoryBeat, sessionId: string | null): string | null {
  if (beat.reviewStatus !== "Approved") return "ยืนยันฉากนี้ก่อน AI ถึงจะใช้ได้";
  if (!sessionId) return "เลือกเซสชันก่อน";
  if (beat.progress === "Active") return "เป็นฉากปัจจุบันอยู่แล้ว";
  return null;
}

// ── อัปโหลดเอกสาร ─────────────────────────────────────────────────────────────

/** ต้องตรงกับเพดานฝั่ง server (`StoryImportEndpoints`) — ตรงนี้แค่เตือนเร็ว server เป็นคนตัดสินจริง */
export const MAX_IMPORT_FILES = 10;
export const MAX_PDF_BYTES = 15 * 1024 * 1024;
export const MAX_IMAGE_BYTES = 3_932_160;
export const IMPORT_ACCEPT = "application/pdf,image/png,image/jpeg,image/webp";

export function checkImportFiles(files: { size: number; type: string }[]): string | null {
  if (files.length === 0) return "เลือกไฟล์ก่อน";
  if (files.length > MAX_IMPORT_FILES) return `อัปโหลดได้ครั้งละไม่เกิน ${MAX_IMPORT_FILES} ไฟล์`;
  for (const file of files) {
    if (file.type === "application/pdf") {
      if (file.size > MAX_PDF_BYTES) return "PDF ใหญ่เกิน 15MB — แยกเป็นบท ๆ แล้วอัปโหลดทีละส่วน";
    } else if (file.size > MAX_IMAGE_BYTES) {
      return "ภาพใหญ่เกิน 3.75MB — ย่อขนาดภาพหรือรวมเป็น PDF";
    }
  }
  return null;
}

const IMPORT_ERRORS: Record<string, string> = {
  file_required: "เลือกไฟล์ก่อน",
  too_many_files: `อัปโหลดได้ครั้งละไม่เกิน ${MAX_IMPORT_FILES} ไฟล์`,
  file_too_large: "ไฟล์ใหญ่เกิน 15MB — แยกเป็นบท ๆ แล้วอัปโหลดทีละส่วน",
  image_too_large: "ภาพใหญ่เกิน 3.75MB — ย่อขนาดภาพหรือรวมเป็น PDF",
  invalid_file_type: "รับเฉพาะ PDF หรือภาพ PNG/JPEG/WebP",
  dm_api_key_not_set: "ตั้ง Anthropic API key ด้านบนก่อน — AI ใช้คีย์ของคุณอ่านเอกสาร",
  quota_exceeded: "พื้นที่เก็บไฟล์ของบัญชีเต็มแล้ว",
  extraction_failed: "AI อ่านเอกสารไม่สำเร็จ — PDF อาจยาวเกิน 100 หน้า หรือเครือข่ายขัดข้อง ลองใหม่หรือแบ่งไฟล์",
  extraction_unreadable: "AI ตอบกลับมาในรูปแบบที่อ่านไม่ได้ ลองใหม่อีกครั้ง",
  no_beats_found: "AI ไม่พบเนื้อเรื่องในไฟล์นี้ — ลองไฟล์ที่เป็นหน้าเนื้อเรื่องจริง หรือภาพที่ชัดขึ้น",
};

export function importErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const code = (error.body as { error?: string } | null)?.error;
    if (code && IMPORT_ERRORS[code]) return IMPORT_ERRORS[code];
    if (error.status === 403) return "เฉพาะ DM ของแคมเปญนี้เท่านั้น";
  }
  return "อัปโหลดไม่สำเร็จ ลองใหม่อีกครั้ง";
}

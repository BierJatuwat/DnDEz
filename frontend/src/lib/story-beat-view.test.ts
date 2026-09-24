import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import {
  activationBlocker,
  checkImportFiles,
  importErrorMessage,
  MAX_IMAGE_BYTES,
  MAX_PDF_BYTES,
  moveBeat,
  parseSecrets,
  serializeSecrets,
  splitLines,
  type StoryBeat,
} from "./story-beat-view";

const beat = (overrides: Partial<StoryBeat> = {}): StoryBeat => ({
  id: "b1",
  order: 0,
  title: "ท่าเรือ",
  summary: null,
  objective: null,
  entryTrigger: null,
  exitConditions: [],
  dmNotes: null,
  secrets: "[]",
  progress: "Upcoming",
  reviewStatus: "Approved",
  ...overrides,
});

describe("parseSecrets", () => {
  it("reads the server's snake_case shape", () => {
    expect(parseSecrets('[{"text":"เจ้าเมืองคือหมาป่า","reveal_condition":"คืนเดือนเต็ม"},{"text":"ทางลับ"}]')).toEqual([
      { text: "เจ้าเมืองคือหมาป่า", revealCondition: "คืนเดือนเต็ม" },
      { text: "ทางลับ", revealCondition: "" },
    ]);
  });

  it("returns nothing for broken or unexpected json instead of throwing", () => {
    expect(parseSecrets("not json")).toEqual([]);
    expect(parseSecrets('{"text":"x"}')).toEqual([]);
    expect(parseSecrets('[1, null, {"reveal_condition":"no text"}]')).toEqual([]);
    expect(parseSecrets(null)).toEqual([]);
  });
});

describe("serializeSecrets", () => {
  it("writes the shape the server validates and drops blank rows", () => {
    const json = serializeSecrets([
      { text: "  เจ้าเมืองคือหมาป่า ", revealCondition: " คืนเดือนเต็ม " },
      { text: "   ", revealCondition: "ไม่มีข้อความ" },
      { text: "ทางลับ", revealCondition: "" },
    ]);
    expect(JSON.parse(json)).toEqual([
      { text: "เจ้าเมืองคือหมาป่า", reveal_condition: "คืนเดือนเต็ม" },
      { text: "ทางลับ" },
    ]);
  });

  it("round-trips through parseSecrets", () => {
    const secrets = [{ text: "ทางลับ", revealCondition: "ค้นกำแพง DC 15" }];
    expect(parseSecrets(serializeSecrets(secrets))).toEqual(secrets);
  });
});

describe("splitLines", () => {
  it("keeps one trimmed condition per non-empty line", () => {
    expect(splitLines(" หนีได้ \n\n  ก็อบลินตายหมด\n")).toEqual(["หนีได้", "ก็อบลินตายหมด"]);
  });
});

describe("moveBeat", () => {
  const ids = ["a", "b", "c"];

  it("swaps with the neighbour", () => {
    expect(moveBeat(ids, "b", -1)).toEqual(["b", "a", "c"]);
    expect(moveBeat(ids, "b", 1)).toEqual(["a", "c", "b"]);
  });

  it("refuses to move past either end or an unknown id", () => {
    expect(moveBeat(ids, "a", -1)).toBeNull();
    expect(moveBeat(ids, "c", 1)).toBeNull();
    expect(moveBeat(ids, "zzz", 1)).toBeNull();
  });
});

describe("activationBlocker", () => {
  it("blocks unreviewed beats even with a session selected", () => {
    expect(activationBlocker(beat({ reviewStatus: "Pending" }), "s1")).toMatch(/ยืนยัน/);
  });

  it("needs a session and a beat that is not already active", () => {
    expect(activationBlocker(beat(), null)).toMatch(/เซสชัน/);
    expect(activationBlocker(beat({ progress: "Active" }), "s1")).toMatch(/อยู่แล้ว/);
    expect(activationBlocker(beat(), "s1")).toBeNull();
  });
});

describe("checkImportFiles", () => {
  it("accepts a pdf up to the limit and images up to theirs", () => {
    expect(checkImportFiles([{ type: "application/pdf", size: MAX_PDF_BYTES }])).toBeNull();
    expect(checkImportFiles([{ type: "image/jpeg", size: MAX_IMAGE_BYTES }])).toBeNull();
  });

  it("rejects oversize files, too many files, and nothing at all", () => {
    expect(checkImportFiles([{ type: "application/pdf", size: MAX_PDF_BYTES + 1 }])).toMatch(/15MB/);
    expect(checkImportFiles([{ type: "image/png", size: MAX_IMAGE_BYTES + 1 }])).toMatch(/3\.75MB/);
    expect(checkImportFiles(Array.from({ length: 11 }, () => ({ type: "image/png", size: 1 })))).toMatch(/10/);
    expect(checkImportFiles([])).not.toBeNull();
  });
});

describe("importErrorMessage", () => {
  it("maps server error codes to guidance", () => {
    expect(importErrorMessage(new ApiError(400, { error: "dm_api_key_not_set" }))).toMatch(/API key/);
    expect(importErrorMessage(new ApiError(422, { error: "no_beats_found" }))).toMatch(/ไม่พบเนื้อเรื่อง/);
  });

  it("falls back for unknown failures", () => {
    expect(importErrorMessage(new ApiError(500, null))).toMatch(/ลองใหม่/);
    expect(importErrorMessage(new Error("network"))).toMatch(/ลองใหม่/);
  });
});

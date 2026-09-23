import { describe, it, expect } from "vitest";
import { ApiError } from "./api";
import { describeSummaryError } from "./ai-summary-view";

describe("describeSummaryError", () => {
  it("maps server error codes to Thai messages the DM can act on", () => {
    expect(describeSummaryError(new ApiError(400, { error: "dm_api_key_not_set" }))).toContain("API key");
    expect(describeSummaryError(new ApiError(400, { error: "session_has_no_messages" }))).toContain(
      "ยังไม่มีบทสนทนา",
    );
    expect(describeSummaryError(new ApiError(400, { error: "session_not_in_campaign" }))).toContain(
      "ไม่ได้อยู่ในแคมเปญนี้",
    );
  });

  it("explains 403 as DM-only", () => {
    expect(describeSummaryError(new ApiError(403, null))).toContain("DM");
  });

  it("falls back to a generic message for unknown codes, bodies and non-API errors", () => {
    const generic = "ทำไม่สำเร็จ ลองใหม่อีกครั้ง";
    expect(describeSummaryError(new ApiError(400, { error: "something_new" }))).toBe(generic);
    expect(describeSummaryError(new ApiError(500, "boom"))).toBe(generic);
    expect(describeSummaryError(new Error("network"))).toBe(generic);
  });
});

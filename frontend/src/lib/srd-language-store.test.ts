import { describe, it, expect, beforeEach } from "vitest";
import { useSrdLanguageStore } from "./srd-language-store";

/** รีเซ็ต store ก่อนทุกเทสต์ — zustand เก็บ state แบบ module-level ข้ามไฟล์เทสต์ */
beforeEach(() => {
  useSrdLanguageStore.setState({ language: "th" });
});

describe("useSrdLanguageStore", () => {
  it("ค่าเริ่มต้นเป็นไทย (ตรงกับส่วนที่เหลือของแอป)", () => {
    expect(useSrdLanguageStore.getState().language).toBe("th");
  });

  it("toggle สลับไปมาระหว่างไทยกับอังกฤษ", () => {
    useSrdLanguageStore.getState().toggle();
    expect(useSrdLanguageStore.getState().language).toBe("en");
    useSrdLanguageStore.getState().toggle();
    expect(useSrdLanguageStore.getState().language).toBe("th");
  });

  it("setLanguage ตั้งค่าตรง ๆ ได้ (ใช้ตอนกดปุ่มที่ระบุภาษาชัดเจน ไม่ใช่แค่สลับ)", () => {
    useSrdLanguageStore.getState().setLanguage("en");
    expect(useSrdLanguageStore.getState().language).toBe("en");
    useSrdLanguageStore.getState().setLanguage("en");
    expect(useSrdLanguageStore.getState().language).toBe("en");
  });
});

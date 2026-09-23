import { create } from "zustand";
import { persist, createJSONStorage } from "zustand/middleware";

/**
 * ภาษาที่ใช้แสดง**เนื้อหาคลัง SRD** (มอนสเตอร์/เวท/สถานะ/กฎ) — ไม่ใช่ภาษาทั้งแอป
 *
 * ตั้งชื่อให้ชัดว่า "srd" ไม่ใช่ "ui" เพราะป้าย/ปุ่ม/ข้อความอื่นของแอปเป็นไทยล้วนอยู่แล้วและ hardcode
 * ไว้ในโค้ด (ไม่มีระบบ i18n ของทั้งแอป) — ตัวนี้คุมเฉพาะเนื้อหาที่มาจาก `/srd/*` ซึ่งมีทั้งสองภาษาให้เลือกจริง
 *
 * **ค่าเริ่มต้นเป็นไทย** ให้ตรงกับส่วนที่เหลือของแอป แต่ **API เริ่มต้นเป็นอังกฤษเสมอ** (ดู doc comment
 * ของ `SrdEndpoints.NormalizeLang` ฝั่ง C# — ต้องคงพฤติกรรมเดิมไว้ให้ผู้เรียกที่ไม่รู้จัก `lang` ไม่พัง)
 * ความต่างนี้ตั้งใจ: หน้าเว็บเป็นฝ่ายขอภาษาไทยชัดเจนทุกครั้งผ่าน hook ใน `use-srd.ts` ไม่ได้พึ่ง default ของ API
 *
 * persist ไว้ใน localStorage เพื่อให้เลือกครั้งเดียวแล้วจำข้ามเซสชัน — ปุ่มสลับอยู่ในทุกพาเนลที่โชว์เนื้อหา
 * SRD (`MonsterLibraryPanel`, `RulesReferencePanel`) และสลับได้ทุกเมื่อ ไม่ใช่แค่ตอนเปิดหน้าครั้งแรก
 */
export type SrdLanguage = "th" | "en";

type SrdLanguageState = {
  language: SrdLanguage;
  toggle: () => void;
  setLanguage: (language: SrdLanguage) => void;
};

export const useSrdLanguageStore = create<SrdLanguageState>()(
  persist(
    (set, get) => ({
      language: "th",
      toggle: () => set({ language: get().language === "th" ? "en" : "th" }),
      setLanguage: (language) => set({ language }),
    }),
    {
      name: "dndez-srd-language",
      storage: createJSONStorage(() => localStorage),
    },
  ),
);

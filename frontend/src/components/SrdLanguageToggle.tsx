"use client";

import { useSrdLanguageStore } from "@/lib/srd-language-store";

/**
 * ปุ่มสลับภาษาเนื้อหา SRD (ไทย/อังกฤษ) — สลับได้ทุกเมื่อ ไม่ใช่แค่ตอนเปิดหน้าครั้งแรก
 *
 * วางไว้ในทุกพาเนลที่โชว์เนื้อหา SRD (`MonsterLibraryPanel`, `RulesReferencePanel`) — สถานะอยู่ใน store
 * เดียวกัน กดที่ไหนก็สลับทั้งสองพาเนลพร้อมกัน (ผู้ใช้คาดหวังว่า "ภาษาที่เลือก" เป็นการตั้งค่าเดียว ไม่ใช่
 * ต้องสลับทีละพาเนล) ดู doc comment เต็มใน `srd-language-store.ts`
 */
export function SrdLanguageToggle() {
  const language = useSrdLanguageStore((s) => s.language);
  const toggle = useSrdLanguageStore((s) => s.toggle);

  return (
    <div className="inline-flex rounded border border-black/20 dark:border-white/20 text-xs overflow-hidden" role="group" aria-label="ภาษาของเนื้อหา SRD">
      <button
        type="button"
        onClick={() => language !== "th" && toggle()}
        aria-pressed={language === "th"}
        className={`px-2 py-1 transition-colors ${
          language === "th"
            ? "bg-indigo-600 text-white"
            : "opacity-60 hover:opacity-100"
        }`}
      >
        ไทย
      </button>
      <button
        type="button"
        onClick={() => language !== "en" && toggle()}
        aria-pressed={language === "en"}
        className={`px-2 py-1 transition-colors border-l border-black/20 dark:border-white/20 ${
          language === "en"
            ? "bg-indigo-600 text-white"
            : "opacity-60 hover:opacity-100"
        }`}
      >
        EN
      </button>
    </div>
  );
}

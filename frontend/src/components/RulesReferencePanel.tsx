"use client";

import { useState } from "react";
import { useSrdCorpus, useSrdCorpusDetail } from "@/lib/use-srd";
import { corpusCategoryLabel, corpusSubtitle } from "@/lib/srd-view";
import { SrdAttribution } from "./SrdAttribution";
import { SrdLanguageToggle } from "./SrdLanguageToggle";
import type { SrdCorpusItem } from "@/lib/srd-types";

const chipClass = "rounded px-2 py-1 text-xs border transition-colors";
const inputClass =
  "rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";

const CATEGORIES: { value: string; label: string; hint: string }[] = [
  { value: "condition", label: "สถานะ", hint: "โดนพิษ ล้มลง มองไม่เห็น — อ่านว่าแต่ละอย่างทำให้เกิดอะไร" },
  { value: "spell", label: "เวท", hint: "คำอธิบายเวททั้งหมดใน SRD" },
  { value: "rule", label: "กฎ", hint: "แอ็กชันพื้นฐานและคำศัพท์ที่กฎอ้างถึง" },
];

/**
 * คู่มือกฎจาก SRD 5.2.1 — สถานะ / เวท / กฎ
 *
 * **ไม่เรียก AI เลย** ตามที่ PROJECT-CONTEXT §2 ล็อกไว้ ("Condition tooltip ดึงจาก RulesCorpus ไม่เรียก AI
 * — ไม่เปลือง token") · ข้อมูลมาจากตาราง `rules_corpus_entries` ที่ฝังมากับระบบตั้งแต่ deploy
 *
 * **เปิดให้ทุกคนที่ล็อกอิน** ไม่ใช่ DM-only — เนื้อหา SRD เป็นกฎสาธารณะที่ผู้เล่นต้องอ่านได้ (ต่างจากคลัง
 * มอนสเตอร์ของแคมเปญซึ่งเป็นข้อมูลเตรียมฉากของ DM)
 *
 * **novice check**: ผู้เล่นที่ไม่รู้กฎกดแท็บ "สถานะ" แล้วเห็นรายการทั้ง 15 อย่างพร้อมคำอธิบายเต็ม
 * โดยไม่ต้องรู้คำว่า "condition" หรือพิมพ์อะไรก่อนเลย
 */
export function RulesReferencePanel() {
  const [category, setCategory] = useState("condition");
  const [query, setQuery] = useState("");
  const [openKey, setOpenKey] = useState<string | null>(null);

  const { items, total, attribution, isLoading, isError } = useSrdCorpus({ category, query });
  const activeCategory = CATEGORIES.find((c) => c.value === category);

  return (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-2">
        <div>
          <h3 className="font-medium">คู่มือกฎ</h3>
          <p className="text-xs opacity-60">{activeCategory?.hint}</p>
        </div>
        <SrdLanguageToggle />
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex gap-1" role="group" aria-label="หมวดของคู่มือ">
          {CATEGORIES.map(({ value, label }) => (
            <button
              key={value}
              type="button"
              onClick={() => {
                setCategory(value);
                setOpenKey(null);
              }}
              aria-pressed={category === value}
              className={`${chipClass} ${
                category === value
                  ? "border-indigo-500 bg-indigo-500/15 text-indigo-600 dark:text-indigo-400"
                  : "border-black/20 dark:border-white/20 opacity-60"
              }`}
            >
              {label}
            </button>
          ))}
        </div>
        <input
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpenKey(null);
          }}
          placeholder="ค้นชื่อ"
          className={`flex-1 min-w-[8rem] ${inputClass}`}
          aria-label="ค้นหาในคู่มือกฎ"
        />
      </div>

      {isError && <p className="text-sm text-red-500">โหลดคู่มือกฎไม่สำเร็จ</p>}
      {isLoading && <p className="text-sm opacity-50">กำลังโหลด...</p>}

      {!isLoading && !isError && items.length === 0 && (
        <p className="text-sm opacity-60">ไม่พบรายการที่ตรงกับที่ค้น</p>
      )}

      {items.length > 0 && (
        <>
          <p className="text-xs opacity-50">
            {corpusCategoryLabel(category)} {total} รายการ
            {total > items.length ? ` (แสดง ${items.length} รายการแรก — พิมพ์ค้นเพื่อแคบลง)` : ""}
          </p>
          <ul className="divide-y divide-black/5 dark:divide-white/5 max-h-96 overflow-y-auto">
            {items.map((item) => (
              <CorpusRow
                key={`${item.category}/${item.key}`}
                item={item}
                open={openKey === item.key}
                onToggle={() => setOpenKey(openKey === item.key ? null : item.key)}
              />
            ))}
          </ul>
        </>
      )}

      <SrdAttribution text={attribution} />
    </div>
  );
}

function CorpusRow({
  item,
  open,
  onToggle,
}: {
  item: SrdCorpusItem;
  open: boolean;
  onToggle: () => void;
}) {
  return (
    <li className="py-1.5">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={open}
        className="w-full text-left hover:opacity-80"
      >
        <span className="text-sm font-medium">{item.title}</span>
        <span className="ml-2 text-xs opacity-50">{corpusSubtitle(item)}</span>
        {/* เห็นตั้งแต่ยังไม่กางว่ารายการนี้เนื้อหาเต็มเป็นอังกฤษอยู่ — กันเสียเวลากางแล้วผิดหวัง */}
        {item.contentTranslated === false && (
          <span className="ml-2 text-[10px] rounded bg-amber-500/10 text-amber-700 dark:text-amber-400 px-1.5 py-0.5">
            EN
          </span>
        )}
      </button>
      {open && <CorpusBody category={item.category} entryKey={item.key} />}
    </li>
  );
}

/** เนื้อหาเต็ม — โหลดตอนกางเท่านั้น (เวทมี 339 รายการ ดึงเนื้อหาทุกตัวล่วงหน้าไม่มีเหตุผล) */
function CorpusBody({ category, entryKey }: { category: string; entryKey: string }) {
  const { content, contentTranslated, isLoading } = useSrdCorpusDetail(category, entryKey);

  if (isLoading) return <p className="mt-1 text-xs opacity-50">กำลังโหลด...</p>;
  if (!content) return null;

  return (
    <div className="mt-1 space-y-2 text-xs leading-relaxed opacity-80">
      {contentTranslated === false && (
        <p className="rounded bg-amber-500/10 text-amber-700 dark:text-amber-400 px-2 py-1 opacity-100">
          รายการนี้ยังไม่มีคำแปล — แสดงเป็นภาษาอังกฤษ
        </p>
      )}
      {/* ต้นฉบับคั่นย่อหน้าด้วยบรรทัดว่าง — แสดงเป็นย่อหน้าจริง ไม่ใช่ก้อนเดียวที่อ่านยาก */}
      {content.split("\n\n").map((paragraph, i) => (
        <p key={i}>{paragraph}</p>
      ))}
    </div>
  );
}

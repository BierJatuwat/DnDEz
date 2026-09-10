"use client";

import { useState } from "react";
import { useCharacterPresets } from "@/lib/use-character-presets";
import { topAbilityScores } from "@/lib/character-preset-view";
import type { CharacterPreset } from "@/lib/character-preset-types";

const cardClass =
  "flex flex-col gap-3 rounded-lg border border-black/10 dark:border-white/10 p-4 bg-black/[0.02] dark:bg-white/[0.02]";
const statPillClass =
  "flex items-center justify-between rounded border border-black/10 dark:border-white/10 px-2 py-1 text-xs";

/**
 * Sprint G Part 7 — "CHOOSE YOUR HERO" (design doc §5, mockup `uxpilot-character-prep.png`)
 *
 * แทนที่ฟอร์ม "กรอกชื่อเปล่า" เดิมด้วยการ์ด preset — เลือกแล้วตั้งชื่อ แล้วได้ตัวละครจริงทันที
 * (novice check ของ Part นี้: เห็น HP/AC จริงบนการ์ดตั้งแต่ก่อนกดเลือกด้วยซ้ำ ไม่ต้องคำนวณเอง)
 *
 * ปุ่ม "สร้างใหม่เอง" เป็น placeholder ตามที่ระบุไว้ว่าทำได้ (ห้ามทำ builder เต็มรูปรอบนี้) —
 * แสดงไว้ให้เห็นว่ามีทางนี้ในอนาคต แต่กดแล้วไม่ทำอะไร ไม่ใช่ลืมทำปุ่ม
 */
export function CharacterPresetPicker({
  campaignId,
  onCreated,
}: {
  campaignId: string;
  onCreated?: () => void;
}) {
  const { presets, isLoading, isError, createFromPreset } = useCharacterPresets(campaignId);
  const [selectedPresetId, setSelectedPresetId] = useState<string | null>(null);
  const [name, setName] = useState("");

  const selectedPreset = presets.find((p) => p.id === selectedPresetId) ?? null;

  const handleConfirm = () => {
    const trimmed = name.trim();
    if (!trimmed || !selectedPresetId) return;
    createFromPreset.mutate(
      { name: trimmed, presetId: selectedPresetId },
      {
        onSuccess: () => {
          setSelectedPresetId(null);
          setName("");
          onCreated?.();
        },
      },
    );
  };

  if (isLoading) {
    return <p className="text-sm opacity-50">กำลังโหลดคลังตัวละคร...</p>;
  }

  if (isError) {
    return <p className="text-sm text-red-500">โหลดคลัง preset ไม่สำเร็จ</p>;
  }

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        {presets.map((preset) => (
          <PresetCard
            key={preset.id}
            preset={preset}
            selected={preset.id === selectedPresetId}
            onSelect={() => setSelectedPresetId(preset.id)}
          />
        ))}

        {/* placeholder ตามสเปก — ไม่มี onClick ที่ทำอะไรจริง */}
        <div className={`${cardClass} items-center justify-center text-center opacity-50 border-dashed`}>
          <span className="text-2xl">+</span>
          <p className="text-sm font-medium">สร้างใหม่เอง</p>
          <p className="text-xs opacity-70">กำหนดค่าเองทุกอย่าง (สำหรับผู้เล่นชำนาญ) — เร็ว ๆ นี้</p>
        </div>
      </div>

      {selectedPreset && (
        <div className={`${cardClass} border-indigo-500`}>
          <p className="text-sm">
            ตั้งชื่อให้ <strong>{selectedPreset.name}</strong> ของคุณ
          </p>
          <div className="flex gap-2">
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="ชื่อตัวละคร"
              className="flex-1 rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-1.5"
            />
            <button
              type="button"
              onClick={handleConfirm}
              disabled={!name.trim() || createFromPreset.isPending}
              className="rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50"
            >
              ยืนยัน
            </button>
          </div>
          {createFromPreset.isError && (
            <p className="text-xs text-red-500">สร้างตัวละครไม่สำเร็จ ลองใหม่อีกครั้ง</p>
          )}
        </div>
      )}
    </div>
  );
}

function PresetCard({
  preset,
  selected,
  onSelect,
}: {
  preset: CharacterPreset;
  selected: boolean;
  onSelect: () => void;
}) {
  const topStats = topAbilityScores(preset.abilityScores);

  return (
    <button
      type="button"
      onClick={onSelect}
      className={`${cardClass} text-left transition-colors ${
        selected
          ? "border-indigo-500 ring-1 ring-indigo-500"
          : "hover:border-black/30 dark:hover:border-white/30"
      }`}
    >
      <div>
        <h3 className="font-semibold">{preset.name}</h3>
        <p className="text-xs opacity-50">Level {preset.level}</p>
      </div>

      <div className="grid grid-cols-2 gap-2">
        <div className={statPillClass}>
          <span className="opacity-60">HP</span>
          <span className="font-medium tabular-nums">{preset.hitPoints ?? "?"}</span>
        </div>
        <div className={statPillClass}>
          <span className="opacity-60">AC</span>
          <span className="font-medium tabular-nums">{preset.armorClass ?? "?"}</span>
        </div>
        {topStats.map((stat) => (
          <div key={stat.label} className={statPillClass}>
            <span className="opacity-60">{stat.label}</span>
            <span className="font-medium tabular-nums text-green-600 dark:text-green-400">
              {stat.modifier}
            </span>
          </div>
        ))}
      </div>
    </button>
  );
}

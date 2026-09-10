import { describe, it, expect } from "vitest";
import { abilityModifier, formatModifier, topAbilityScores } from "./character-preset-view";
import type { PresetAbilityScores } from "./character-preset-types";

describe("abilityModifier", () => {
  it("คำนวณตามกฎ 5e มาตรฐาน floor((score-10)/2)", () => {
    expect(abilityModifier(15)).toBe(2);
    expect(abilityModifier(10)).toBe(0);
    expect(abilityModifier(8)).toBe(-1);
    expect(abilityModifier(13)).toBe(1); // floor(1.5) = 1 ไม่ใช่ round เป็น 2
  });
});

describe("formatModifier", () => {
  it("ค่าบวกมีเครื่องหมาย + เสมอ รวมถึงศูนย์", () => {
    expect(formatModifier(3)).toBe("+3");
    expect(formatModifier(0)).toBe("+0");
  });

  it("ค่าลบมีเครื่องหมาย - อยู่แล้วในตัวเลข", () => {
    expect(formatModifier(-1)).toBe("-1");
  });
});

describe("topAbilityScores", () => {
  const fighter: PresetAbilityScores = {
    strength: 15,
    dexterity: 13,
    constitution: 14,
    intelligence: 12,
    wisdom: 10,
    charisma: 8,
  };

  it("เลือกสองอันดับแรกเรียงจากสูงไปต่ำ", () => {
    const top = topAbilityScores(fighter);
    expect(top).toEqual([
      { label: "STR", value: 15, modifier: "+2" },
      { label: "CON", value: 14, modifier: "+2" },
    ]);
  });

  it("ไม่มีข้อมูลคืน array ว่าง ไม่ throw", () => {
    expect(topAbilityScores(null)).toEqual([]);
  });

  it("รับ count เพื่อขอมากกว่า 2 ได้", () => {
    expect(topAbilityScores(fighter, 3).map((s) => s.label)).toEqual(["STR", "CON", "DEX"]);
  });
});

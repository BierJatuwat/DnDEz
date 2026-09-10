import { describe, it, expect } from "vitest";
import {
  advantageBadge,
  costLabel,
  groupByOption,
  spentResourceLabels,
  targetLine,
} from "./affordance-view";
import type { AffordanceItem } from "./affordance-types";

function item(overrides: Partial<AffordanceItem> = {}): AffordanceItem {
  return {
    optionId: "opt-1",
    optionName: "Longsword",
    targetId: "goblin",
    targetName: "ก็อบลินยาม",
    advantageMode: "None",
    cost: "Action",
    distanceFeet: 5,
    notes: [],
    ...overrides,
  };
}

describe("costLabel", () => {
  it("ใช้ถ้อยคำเดียวกับ mockup", () => {
    expect(costLabel("Action")).toBe("1 Action");
    expect(costLabel("BonusAction")).toBe("Bonus Action");
  });

  it("ท่าที่ไม่กิน action economy บอกให้ชัดว่าไม่เสียอะไร", () => {
    expect(costLabel("None")).toBe("ไม่ใช้ action");
  });
});

describe("advantageBadge", () => {
  it("ทอยปกติไม่ต้องมีป้าย", () => {
    expect(advantageBadge("None")).toBeNull();
  });

  it("มีป้ายเฉพาะตอนได้เปรียบ/เสียเปรียบ", () => {
    expect(advantageBadge("Disadvantage")).toBe("DISADVANTAGE");
    expect(advantageBadge("Advantage")).toBe("ADVANTAGE");
  });
});

describe("targetLine", () => {
  it("บอกทั้งชื่อเป้าและระยะ", () => {
    expect(targetLine(item())).toBe("ก็อบลินยาม · ห่าง 5 ฟุต");
  });

  it("ไม่มีระยะก็ยังบอกชื่อเป้าได้", () => {
    expect(targetLine(item({ distanceFeet: null }))).toBe("ก็อบลินยาม");
  });

  it("ท่าที่ไม่ได้เล็งใคร (เช่นลงจุดบนแผนที่) ไม่มีบรรทัดเป้า", () => {
    expect(targetLine(item({ targetId: null, targetName: null }))).toBeNull();
  });
});

describe("groupByOption", () => {
  it("ท่าเดียวกันหลายเป้ารวมเป็นการ์ดใบเดียว แต่เก็บเป้าไว้ครบ", () => {
    const groups = groupByOption([
      item({ targetId: "goblin", targetName: "ก็อบลิน" }),
      item({ targetId: "ogre", targetName: "ออร์ค" }),
    ]);

    expect(groups).toHaveLength(1);
    expect(groups[0].targets.map((t) => t.targetName)).toEqual(["ก็อบลิน", "ออร์ค"]);
  });

  it("คนละท่าอยู่คนละใบ และเรียงตามลำดับที่ server ส่งมา ไม่เรียงใหม่เอง", () => {
    const groups = groupByOption([
      item({ optionId: "bow", optionName: "Shortbow" }),
      item({ optionId: "sword", optionName: "Longsword" }),
    ]);

    expect(groups.map((g) => g.optionName)).toEqual(["Shortbow", "Longsword"]);
  });

  it("ลิสต์ว่างไม่ throw", () => {
    expect(groupByOption([])).toEqual([]);
  });
});

describe("spentResourceLabels", () => {
  it("ไม่มี turn state (นอกการต่อสู้) = ไม่ต้องบอกว่าใช้อะไรไปแล้ว", () => {
    expect(spentResourceLabels(null)).toEqual([]);
  });

  it("บอกเฉพาะทรัพยากรที่ใช้ไปแล้วจริง", () => {
    expect(
      spentResourceLabels({
        actionAvailable: false,
        bonusActionAvailable: true,
        reactionAvailable: false,
      }),
    ).toEqual(["Action", "Reaction"]);
  });

  it("ยังไม่ได้ใช้อะไรเลย = ไม่มีอะไรต้องแสดง", () => {
    expect(
      spentResourceLabels({
        actionAvailable: true,
        bonusActionAvailable: true,
        reactionAvailable: true,
      }),
    ).toEqual([]);
  });
});

import { describe, it, expect } from "vitest";
import {
  abilityRows,
  challengeRatingValue,
  corpusSubtitle,
  difficultyLabel,
  difficultyTone,
  monsterActionGroups,
  monsterSubtitle,
  signed,
  sortByDifficulty,
  spellLevelLabel,
  tokenLabel,
} from "./srd-view";
import type { SrdCorpusItem, SrdMonsterDetail, SrdMonsterSummary } from "./srd-types";

function monster(over: Partial<SrdMonsterSummary> = {}): SrdMonsterSummary {
  return {
    key: "goblin-warrior",
    name: "Goblin Warrior",
    size: "Small",
    type: "Fey",
    alignment: "Chaotic Neutral",
    armorClass: 15,
    hitPoints: 10,
    speedFeet: 30,
    reachFeet: 5,
    challengeRating: "1/4",
    xp: 50,
    ...over,
  };
}

function detail(over: Partial<SrdMonsterDetail["data"]> = {}): SrdMonsterDetail {
  return {
    ...monster(),
    sourceVersion: "5.2.1",
    attribution: "…",
    data: {
      abilities: {
        str: { score: 8, mod: -1, save: -1 },
        dex: { score: 15, mod: 2, save: 2 },
        con: { score: 10, mod: 0, save: 0 },
        int: { score: 10, mod: 0, save: 0 },
        wis: { score: 8, mod: -1, save: -1 },
        cha: { score: 8, mod: -1, save: -1 },
      },
      ...over,
    },
  };
}

describe("challengeRatingValue", () => {
  it("อ่านเศษส่วนของ SRD ได้ (CR ไม่ใช่ตัวเลขทศนิยมในต้นฉบับ)", () => {
    expect(challengeRatingValue("1/8")).toBeCloseTo(0.125);
    expect(challengeRatingValue("1/4")).toBeCloseTo(0.25);
    expect(challengeRatingValue("1/2")).toBeCloseTo(0.5);
  });

  it("อ่านจำนวนเต็มและช่องว่างส่วนเกินได้", () => {
    expect(challengeRatingValue("3")).toBe(3);
    expect(challengeRatingValue(" 30 ")).toBe(30);
    expect(challengeRatingValue("0")).toBe(0);
  });

  it("คืน null เมื่ออ่านไม่ออก แทนการคืน NaN หรือ 0 ที่ทำให้เรียงลำดับเพี้ยนเงียบ ๆ", () => {
    expect(challengeRatingValue("None")).toBeNull();
    expect(challengeRatingValue("")).toBeNull();
    expect(challengeRatingValue("1/0")).toBeNull();
    expect(challengeRatingValue("a/b")).toBeNull();
  });
});

describe("difficultyLabel", () => {
  it("แปลง CR เป็นคำที่ DM มือใหม่อ่านออก", () => {
    expect(difficultyLabel("1/8")).toBe("อ่อน");
    expect(difficultyLabel("1")).toBe("พอสู้ได้");
    expect(difficultyLabel("4")).toBe("พอสู้ได้");
    expect(difficultyLabel("5")).toBe("อันตราย");
    expect(difficultyLabel("10")).toBe("อันตราย");
    expect(difficultyLabel("11")).toBe("อันตรายมาก");
    expect(difficultyLabel("30")).toBe("อันตรายมาก");
  });

  it("CR ที่อ่านไม่ออกไม่ถูกเดาเป็น 'อ่อน'", () => {
    expect(difficultyLabel("None")).toBe("ไม่ระบุ");
    expect(difficultyTone("None")).toBe("muted");
  });
});

describe("sortByDifficulty", () => {
  it("เรียงง่ายไปยากโดยเทียบค่าจริงของเศษส่วน ไม่ใช่เรียงตามตัวอักษร", () => {
    const rows = [
      monster({ name: "Ogre", challengeRating: "2" }),
      monster({ name: "Rat", challengeRating: "1/8" }),
      monster({ name: "Goblin", challengeRating: "1/4" }),
      monster({ name: "Tarrasque", challengeRating: "30" }),
    ];
    expect(sortByDifficulty(rows).map((m) => m.name)).toEqual(["Rat", "Goblin", "Ogre", "Tarrasque"]);
  });

  it("CR เท่ากันเรียงตามชื่อ — ลำดับต้องคงที่ ไม่สลับระหว่าง re-render", () => {
    const rows = [
      monster({ name: "Zombie", challengeRating: "1/4" }),
      monster({ name: "Acolyte", challengeRating: "1/4" }),
    ];
    expect(sortByDifficulty(rows).map((m) => m.name)).toEqual(["Acolyte", "Zombie"]);
  });

  it("ไม่แก้ array เดิม (ผลลัพธ์ของ query ถูกแชร์กับที่อื่น)", () => {
    const rows = [monster({ name: "B", challengeRating: "2" }), monster({ name: "A", challengeRating: "1" })];
    sortByDifficulty(rows);
    expect(rows.map((m) => m.name)).toEqual(["B", "A"]);
  });
});

describe("monsterSubtitle", () => {
  it("สรุปขนาด/ชนิด/ความยากในบรรทัดเดียว", () => {
    expect(monsterSubtitle(monster())).toBe("Small · Fey · CR 1/4");
  });
});

describe("abilityRows", () => {
  it("เรียง STR→CHA เสมอ ไม่ใช่ตามลำดับคีย์ที่ server ส่งมา", () => {
    expect(abilityRows(detail()).map((r) => r.label)).toEqual(["STR", "DEX", "CON", "INT", "WIS", "CHA"]);
  });

  it("modifier มีเครื่องหมายเสมอ", () => {
    const rows = abilityRows(detail());
    expect(rows.find((r) => r.label === "STR")?.modifier).toBe("-1");
    expect(rows.find((r) => r.label === "DEX")?.modifier).toBe("+2");
    expect(rows.find((r) => r.label === "CON")?.modifier).toBe("+0");
  });

  it("ค่าที่ขาดถูกข้าม ไม่แสดงเป็น 0 (0 เป็นค่าที่เป็นไปไม่ได้ จะทำให้เข้าใจผิดว่าอ่อนมาก)", () => {
    const partial = detail();
    delete partial.data.abilities.int;
    expect(abilityRows(partial).map((r) => r.label)).toEqual(["STR", "DEX", "CON", "WIS", "CHA"]);
  });

  it("ไม่มีข้อมูลเลยคืนลิสต์ว่าง ไม่พัง", () => {
    expect(abilityRows(null)).toEqual([]);
  });
});

describe("monsterActionGroups", () => {
  it("เรียงตามลำดับที่อ่านในหนังสือ และตัดหมวดที่ว่างทิ้ง", () => {
    const d = detail({
      actions: [{ kind: "Action", name: "Scimitar", text: "…" }],
      bonusActions: [{ kind: "BonusAction", name: "Nimble Escape", text: "…" }],
      traits: [],
    });
    expect(monsterActionGroups(d).map((g) => g.label)).toEqual(["ท่าโจมตี / แอ็กชัน", "โบนัสแอ็กชัน"]);
  });

  it("ไม่มี detail คืนลิสต์ว่าง", () => {
    expect(monsterActionGroups(null)).toEqual([]);
  });
});

describe("spellLevelLabel", () => {
  it("ระดับ 0 คือ cantrip ไม่ใช่ 'ระดับ 0'", () => {
    expect(spellLevelLabel(0)).toBe("Cantrip");
    expect(spellLevelLabel(1)).toBe("ระดับ 1");
    expect(spellLevelLabel(undefined)).toBe("");
  });
});

describe("corpusSubtitle", () => {
  const spell: SrdCorpusItem = {
    category: "spell",
    key: "magic-missile",
    title: "Magic Missile",
    data: { level: 1, school: "Evocation", classes: ["Sorcerer", "Wizard"] },
  };

  it("เวทบอกระดับ/สำนัก/คลาส", () => {
    expect(corpusSubtitle(spell)).toBe("ระดับ 1 · Evocation · Sorcerer, Wizard");
  });

  it("สถานะใช้ป้ายกำกับจาก data", () => {
    expect(corpusSubtitle({ category: "condition", key: "blinded", title: "Blinded", data: { tag: "Condition" } }))
      .toBe("Condition");
  });

  it("ไม่มีป้ายกำกับก็ยังมีคำอธิบายหมวดให้อ่าน ไม่ใช่ช่องว่าง", () => {
    expect(corpusSubtitle({ category: "rule", key: "cover", title: "Cover", data: {} })).toBe("กฎ");
  });
});

describe("tokenLabel", () => {
  const nameOf = (id: string) => (id === "c-1" ? "ฮีโร่" : undefined);

  it("ใช้ชื่อที่ server ส่งมาก่อนเสมอ", () => {
    expect(tokenLabel({ entityId: "m-1", entityType: "Monster", name: "Goblin Warrior" }, nameOf))
      .toBe("Goblin Warrior");
  });

  it("server รุ่นเก่าไม่ส่งชื่อ — ถอยไปหาในลิสต์ตัวละครเหมือนเดิม", () => {
    expect(tokenLabel({ entityId: "c-1", entityType: "Character" }, nameOf)).toBe("ฮีโร่");
  });

  it("หาชื่อไม่เจอ ต้องไม่โชว์คำว่า 'Monster' ซึ่งเป็นชื่อ enum ของ API", () => {
    expect(tokenLabel({ entityId: "m-9", entityType: "Monster" }, nameOf)).toBe("มอนสเตอร์");
    expect(tokenLabel({ entityId: "x-9", entityType: "Character" }, nameOf)).toBe("ไม่ทราบชื่อ");
  });

  it("ชื่อเป็น null (ไม่ใช่ undefined) ก็ถอยไป fallback เหมือนกัน", () => {
    expect(tokenLabel({ entityId: "c-1", entityType: "Character", name: null }, nameOf)).toBe("ฮีโร่");
  });
});

describe("signed", () => {
  it("มีเครื่องหมายเสมอรวมถึงศูนย์", () => {
    expect(signed(0)).toBe("+0");
    expect(signed(3)).toBe("+3");
    expect(signed(-2)).toBe("-2");
  });
});

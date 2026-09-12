import { describe, it, expect } from "vitest";
import {
  abilityShort,
  attackPlan,
  damageTypeLabel,
  describeDice,
  formulaLine,
} from "./affordance-formula";
import type { AffordanceItem } from "./affordance-types";

/** แถวจาก server ขั้นต่ำ — ฟิลด์สูตรทั้ง 7 ไม่ใส่มาเลย (= response จาก server รุ่นก่อน Sprint H Part 4) */
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

/** ค่าเดียวกับที่ backend คืนให้นักรบ STR 15 ถือดาบยาว (AffordanceFormulaEndpointsTests) */
const longsword: Partial<AffordanceItem> = {
  ability: "Strength",
  attackExpression: "1d20+4",
  damageExpression: "1d8+2",
  damageType: "Slashing",
  saveDc: null,
  saveAbility: null,
  healingExpression: null,
};

describe("formulaLine", () => {
  it("อาวุธ: บอกทั้งทอยโจมตีและดาเมจพร้อมชนิด", () => {
    expect(formulaLine(item(longsword))).toBe("ทอยโจมตี 1d20+4 · ดาเมจ 1d8+2 ฟัน");
  });

  it("เวทแบบให้เป้าเซฟ: บอก DC แทนการทอยโจมตี", () => {
    expect(
      formulaLine(item({ saveDc: 12, saveAbility: "Dexterity", damageExpression: "1d8", damageType: "Radiant" })),
    ).toBe("เป้าทอยเซฟ DEX ต้องได้ 12 · ดาเมจ 1d8 แสง");
  });

  it("เวทรักษา: บอกสูตรการรักษา", () => {
    expect(formulaLine(item({ healingExpression: "2d8+2" }))).toBe("รักษา 2d8+2");
  });

  it("server รุ่นเก่าไม่ส่งฟิลด์สูตรมาเลย → ไม่มีบรรทัดสูตร (ไม่แต่งตัวเลขเอง)", () => {
    expect(formulaLine(item())).toBeNull();
  });

  it("server คำนวณให้ไม่ได้ (ทุกฟิลด์เป็น null) → ไม่มีบรรทัดสูตร", () => {
    expect(
      formulaLine(
        item({
          ability: null,
          attackExpression: null,
          saveDc: null,
          saveAbility: null,
          damageExpression: null,
          damageType: null,
          healingExpression: null,
        }),
      ),
    ).toBeNull();
  });
});

describe("describeDice — แปลสูตรเป็นภาษาคนให้มือใหม่", () => {
  it("ลูกเต๋าหนึ่งลูกบวกค่า", () => {
    expect(describeDice("1d20+4")).toBe("ลูกเต๋า 20 หน้า 1 ลูก แล้วบวก 4");
  });

  it("หลายลูก", () => {
    expect(describeDice("3d4+3")).toBe("ลูกเต๋า 4 หน้า 3 ลูก แล้วบวก 3");
  });

  it("ค่าติดลบ", () => {
    expect(describeDice("1d8-1")).toBe("ลูกเต๋า 8 หน้า 1 ลูก แล้วลบ 1");
  });

  it("ไม่มีค่าบวก", () => {
    expect(describeDice("1d10")).toBe("ลูกเต๋า 10 หน้า 1 ลูก");
  });

  it("รูปที่ server ไม่เคยส่ง → null ไม่เดาความหมาย", () => {
    expect(describeDice("2d6+1d4")).toBeNull();
    expect(describeDice("")).toBeNull();
  });
});

describe("damageTypeLabel", () => {
  it("ชื่อไทยสั้น ๆ ของชนิดที่ใช้บ่อย", () => {
    expect(damageTypeLabel("Slashing")).toBe("ฟัน");
    expect(damageTypeLabel("Piercing")).toBe("แทง");
    expect(damageTypeLabel("Fire")).toBe("ไฟ");
  });

  it("ครบทั้ง 13 ชนิดของ DnDEz.Rules.DamageType ไม่มีตัวไหนตกหล่นเป็นชื่ออังกฤษ", () => {
    const all = [
      "Acid", "Bludgeoning", "Cold", "Fire", "Force", "Lightning", "Necrotic",
      "Piercing", "Poison", "Psychic", "Radiant", "Slashing", "Thunder",
    ];
    for (const name of all) {
      const label = damageTypeLabel(name);
      expect(label).not.toBe("");
      expect(label).not.toBe(name);
    }
  });

  it("ชนิดที่ไม่รู้จักแสดงชื่อเดิม ไม่ซ่อนทิ้ง", () => {
    expect(damageTypeLabel("Sonic")).toBe("Sonic");
  });

  it("ไม่มีชนิด → สตริงว่าง", () => {
    expect(damageTypeLabel(null)).toBe("");
    expect(damageTypeLabel(undefined)).toBe("");
  });
});

describe("abilityShort", () => {
  it("ย่อ 6 ค่าเป็นตัวย่อที่ใช้บนชีท", () => {
    expect(abilityShort("Strength")).toBe("STR");
    expect(abilityShort("Dexterity")).toBe("DEX");
    expect(abilityShort("Constitution")).toBe("CON");
    expect(abilityShort("Intelligence")).toBe("INT");
    expect(abilityShort("Wisdom")).toBe("WIS");
    expect(abilityShort("Charisma")).toBe("CHA");
  });

  it("ไม่มีค่า → สตริงว่าง", () => {
    expect(abilityShort(null)).toBe("");
  });
});

describe("attackPlan — กดแล้วทอยได้เลย หรือต้องให้ DM ทอยให้", () => {
  it("อาวุธที่มีสูตรครบ → ยิงเข้า /attacks ได้ด้วยสูตรของ server ตรง ๆ", () => {
    expect(attackPlan(item(longsword))).toEqual({
      kind: "attack",
      targetId: "goblin",
      expression: "1d20+4",
      damageExpression: "1d8+2",
      damageType: "Slashing",
      advantage: "None",
    });
  });

  /** ก่อน Part นี้การ์ดขึ้น "ทอยแบบเสียเปรียบ" แต่ไม่ได้ส่งโหมดไปกับการโจมตีเลย → ทอยปกติเงียบ ๆ */
  it("ส่งโหมดเสียเปรียบที่ server คำนวณมาไปกับการโจมตีด้วย", () => {
    const plan = attackPlan(item({ ...longsword, advantageMode: "Disadvantage" }));
    expect(plan.kind).toBe("attack");
    expect(plan.kind === "attack" && plan.advantage).toBe("Disadvantage");
  });

  it("เวทแบบให้เป้าเซฟ → ให้ DM ทอยให้ (ยังไม่มี endpoint ทอยเซฟแทนเป้า)", () => {
    const plan = attackPlan(
      item({ saveDc: 12, saveAbility: "Dexterity", damageExpression: "1d8", damageType: "Radiant" }),
    );
    expect(plan.kind).toBe("dm");
    expect(plan.kind === "dm" && plan.reason).toContain("DM");
    expect(plan.kind === "dm" && plan.reason).toContain("DEX");
  });

  it("เวทรักษา → ให้ DM (ยังไม่มี endpoint รักษา)", () => {
    const plan = attackPlan(item({ healingExpression: "2d8+2" }));
    expect(plan.kind).toBe("dm");
    expect(plan.kind === "dm" && plan.reason).toContain("DM");
  });

  it("ท่าที่โดนอัตโนมัติ (มีดาเมจแต่ไม่มีทอยโจมตี) → ให้ DM", () => {
    const plan = attackPlan(item({ damageExpression: "3d4+3", damageType: "Force" }));
    expect(plan.kind).toBe("dm");
  });

  it("ไม่มีสูตรเลย (server รุ่นเก่า / ไม่รู้คลาส) → ให้ DM ไม่เปิดช่องกรอก", () => {
    expect(attackPlan(item()).kind).toBe("dm");
  });

  /** โค้ดเดิม hardcode "Slashing" ทุกท่า — ถ้าไม่รู้ชนิดจริงต้องไม่ส่งไปเดา */
  it("มีสูตรโจมตีแต่ไม่รู้ชนิดดาเมจ → ไม่ส่งไปเดาชนิด", () => {
    expect(attackPlan(item({ ...longsword, damageType: null })).kind).toBe("dm");
  });

  /**
   * Sprint H Part 7 — มอนสเตอร์เข้ารายการเป้าหมายแล้ว แต่ `POST .../attacks` รับได้เฉพาะตัวละคร
   * (มอนสเตอร์ยังไม่มีพลังชีวิตรายตัว) · ถ้าปล่อยให้กดได้ จะยิงไปแล้วเด้ง error — ต้องบอกให้ขอ DM แทน
   */
  it("เป้าที่เป็นมอนสเตอร์ → ให้ DM ทอยให้ ไม่ยิงเข้า /attacks", () => {
    const plan = attackPlan(item({ ...longsword, targetEntityType: "Monster" }));

    expect(plan.kind).toBe("dm");
    expect(plan.kind === "dm" && plan.reason).toContain("DM");
  });

  it("เป้าที่เป็นตัวละคร (หรือ server รุ่นเก่าที่ไม่ส่งชนิดมา) ยังกดทอยได้ตามเดิม", () => {
    expect(attackPlan(item({ ...longsword, targetEntityType: "Character" })).kind).toBe("attack");
    expect(attackPlan(item(longsword)).kind).toBe("attack");
  });

  it("ท่าที่ไม่ได้เล็งใคร → ไม่มีเป้าให้ส่งไป /attacks", () => {
    expect(attackPlan(item({ ...longsword, targetId: null, targetName: null })).kind).toBe("dm");
  });
});

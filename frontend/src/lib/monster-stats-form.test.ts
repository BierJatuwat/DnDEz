import { describe, it, expect } from "vitest";
import {
  draftFromMonster,
  hasChanges,
  hasErrors,
  parseStatDraft,
  type MonsterStatDraft,
} from "./monster-stats-form";
import type { CampaignMonster } from "./srd-types";

const goblin: CampaignMonster = {
  id: "m-1",
  name: "Goblin Warrior",
  armorClass: 15,
  hitPoints: 10,
  speedFeet: 30,
  reachFeet: 5,
  srdKey: "goblin-warrior",
  reviewStatus: "Approved",
};

const draft = (over: Partial<MonsterStatDraft> = {}): MonsterStatDraft => ({
  ...draftFromMonster(goblin),
  ...over,
});

describe("draftFromMonster", () => {
  it("เติมฟอร์มจากค่าปัจจุบัน", () => {
    expect(draftFromMonster(goblin)).toEqual({
      name: "Goblin Warrior",
      armorClass: "15",
      hitPoints: "10",
      speedFeet: "30",
      reachFeet: "5",
    });
  });
});

describe("parseStatDraft — สิ่งที่เปลี่ยนจริงเท่านั้น", () => {
  it("ไม่แตะอะไรเลย = ไม่มีอะไรต้องส่ง", () => {
    const result = parseStatDraft(draft(), goblin);
    expect(result.changes).toEqual({});
    expect(hasChanges(result)).toBe(false);
    expect(hasErrors(result)).toBe(false);
  });

  it("แก้ช่องเดียว ส่งไปช่องเดียว (PATCH ไม่ใช่ PUT)", () => {
    expect(parseStatDraft(draft({ hitPoints: "25" }), goblin).changes).toEqual({ hitPoints: 25 });
  });

  it("แก้หลายช่องพร้อมกันได้", () => {
    expect(parseStatDraft(draft({ name: "ก็อบลินหัวหน้า", reachFeet: "10" }), goblin).changes)
      .toEqual({ name: "ก็อบลินหัวหน้า", reachFeet: 10 });
  });

  it("ตัดช่องว่างหัวท้ายของชื่อ และไม่นับเป็นการแก้ถ้าเหลือค่าเดิม", () => {
    expect(parseStatDraft(draft({ name: "  Goblin Warrior  " }), goblin).changes).toEqual({});
    expect(parseStatDraft(draft({ name: "  ผีดิบ  " }), goblin).changes).toEqual({ name: "ผีดิบ" });
  });

  it("เลขเท่าเดิมแต่พิมพ์ต่างกัน (มีช่องว่าง) ไม่นับเป็นการแก้", () => {
    expect(parseStatDraft(draft({ armorClass: " 15 " }), goblin).changes).toEqual({});
  });
});

describe("parseStatDraft — ค่าที่รับไม่ได้", () => {
  it("ชื่อว่างไม่ได้", () => {
    const result = parseStatDraft(draft({ name: "   " }), goblin);
    expect(result.errors.name).toBeTruthy();
    expect(result.changes).toEqual({});
  });

  it("ช่องตัวเลขว่างไม่ได้ — ต่างจาก 'ไม่แก้' ตรงที่ผู้ใช้ลบค่าทิ้งแล้วยังไม่ใส่กลับ", () => {
    expect(parseStatDraft(draft({ hitPoints: "" }), goblin).errors.hitPoints).toBeTruthy();
  });

  it("ต้องเป็นจำนวนเต็ม — ทศนิยม/สัญกรณ์วิทยาศาสตร์/ฐานสิบหกไม่ใช่สิ่งที่คนตั้งใจพิมพ์", () => {
    expect(parseStatDraft(draft({ hitPoints: "10.5" }), goblin).errors.hitPoints).toBeTruthy();
    expect(parseStatDraft(draft({ hitPoints: "1e3" }), goblin).errors.hitPoints).toBeTruthy();
    expect(parseStatDraft(draft({ hitPoints: "0x10" }), goblin).errors.hitPoints).toBeTruthy();
    expect(parseStatDraft(draft({ hitPoints: "abc" }), goblin).errors.hitPoints).toBeTruthy();
  });

  it("AC/HP ต่ำกว่า 1 ไม่มีความหมายในกฎ", () => {
    expect(parseStatDraft(draft({ armorClass: "0" }), goblin).errors.armorClass).toBeTruthy();
    expect(parseStatDraft(draft({ hitPoints: "0" }), goblin).errors.hitPoints).toBeTruthy();
    expect(parseStatDraft(draft({ armorClass: "-3" }), goblin).errors.armorClass).toBeTruthy();
  });

  it("ระยะเอื้อมต่ำกว่า 5 ฟุตขัด SRD ตรง ๆ", () => {
    expect(parseStatDraft(draft({ reachFeet: "0" }), goblin).errors.reachFeet).toBeTruthy();
    expect(parseStatDraft(draft({ reachFeet: "4" }), goblin).errors.reachFeet).toBeTruthy();
    expect(parseStatDraft(draft({ reachFeet: "5" }), goblin).errors.reachFeet).toBeUndefined();
  });

  it("ระยะเดิน 0 ใช้ได้ — สิ่งมีชีวิตที่ขยับเองไม่ได้มีจริง", () => {
    const result = parseStatDraft(draft({ speedFeet: "0" }), goblin);
    expect(result.errors.speedFeet).toBeUndefined();
    expect(result.changes).toEqual({ speedFeet: 0 });
  });

  it("เกินเพดานที่กันพิมพ์พลาด", () => {
    expect(parseStatDraft(draft({ armorClass: "100" }), goblin).errors.armorClass).toBeTruthy();
    expect(parseStatDraft(draft({ hitPoints: "10000" }), goblin).errors.hitPoints).toBeTruthy();
    expect(parseStatDraft(draft({ reachFeet: "1000" }), goblin).errors.reachFeet).toBeTruthy();
  });

  it("ช่องที่ผิดไม่ถูกส่งไป แต่ช่องอื่นที่ถูกยังนับเป็นการแก้ (ปุ่มบันทึกถูกปิดด้วย hasErrors อยู่แล้ว)", () => {
    const result = parseStatDraft(draft({ armorClass: "0", hitPoints: "20" }), goblin);
    expect(result.changes).toEqual({ hitPoints: 20 });
    expect(hasErrors(result)).toBe(true);
  });
});

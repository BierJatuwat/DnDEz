import { describe, it, expect } from "vitest";
import {
  normalizeHpUpdated,
  applyHpUpdate,
  ownedCharacters,
  attackTargets,
  describeCheckResult,
  describeAttackResult,
  type CharacterSummary,
  type CharacterDetail,
  type CheckResult,
  type AttackResult,
} from "./player-actions";

function makeSummary(over: Partial<CharacterSummary> = {}): CharacterSummary {
  return {
    id: "c-1",
    name: "Aria",
    level: 3,
    ownerUserId: "u-1",
    controlledByDm: false,
    ...over,
  };
}

function makeDetail(over: Partial<CharacterDetail> = {}): CharacterDetail {
  return {
    id: "c-1",
    campaignId: "camp-1",
    name: "Aria",
    level: 3,
    sheet: null,
    ownerUserId: "u-1",
    controlledByDm: false,
    harvestConsent: false,
    isOrphan: false,
    createdAt: "2026-01-01T00:00:00Z",
    currentHp: 20,
    maxHp: 24,
    currentTempHp: 3,
    armorClass: 15,
    damageResistances: [],
    damageVulnerabilities: [],
    damageImmunities: [],
    ...over,
  };
}

function makeCheck(over: Partial<CheckResult> = {}): CheckResult {
  return {
    id: "chk-1",
    characterId: "c-1",
    expression: "1d20+3",
    rolls: [{ sides: 20, value: 11, dropped: false, sign: 1 }],
    total: 14,
    dc: 12,
    success: true,
    ...over,
  };
}

function makeAttack(over: Partial<AttackResult> = {}): AttackResult {
  return {
    id: "atk-1",
    attackerCharacterId: "c-1",
    targetCharacterId: "c-2",
    expression: "1d20+5",
    rolls: [{ sides: 20, value: 13, dropped: false, sign: 1 }],
    total: 18,
    targetArmorClass: 15,
    outcome: "Hit",
    hit: true,
    // ตรงกับ DamageComponentResponse ของ backend จริง (type/rawAmount/adjustedAmount/rolls)
    // ไม่ใช่ shape ของ request ที่ส่งไป (expression/type)
    damageComponents: [
      {
        type: "Slashing",
        rawAmount: 7,
        adjustedAmount: 7,
        rolls: [{ sides: 8, value: 4, dropped: false, sign: 1 }],
      },
    ],
    totalDamage: 7,
    targetPreviousHp: 12,
    targetNewHp: 5,
    targetPreviousTempHp: 0,
    targetNewTempHp: 0,
    targetDroppedToZero: false,
    targetIsInstantDeath: false,
    ...over,
  };
}

describe("normalizeHpUpdated", () => {
  it("narrows a well-formed broadcast payload", () => {
    const event = normalizeHpUpdated({
      characterId: "c-1",
      previousHp: 12,
      newHp: 5,
      previousTempHp: 4,
      newTempHp: 0,
      droppedToZero: false,
      isInstantDeath: false,
    });

    expect(event).toEqual({
      characterId: "c-1",
      previousHp: 12,
      newHp: 5,
      previousTempHp: 4,
      newTempHp: 0,
      droppedToZero: false,
      isInstantDeath: false,
    });
  });

  it("rejects payloads that cannot identify a character or its new hp", () => {
    // ข้อมูลข้ามสายไว้ใจไม่ได้ — narrow runtime แทน cast (แนวเดียวกับ normalizeProposalCreated)
    expect(normalizeHpUpdated(null)).toBeNull();
    expect(normalizeHpUpdated("HpUpdated")).toBeNull();
    expect(normalizeHpUpdated({ newHp: 5, newTempHp: 0 })).toBeNull();
    expect(normalizeHpUpdated({ characterId: "c-1", newTempHp: 0 })).toBeNull();
    expect(normalizeHpUpdated({ characterId: "c-1", newHp: "5", newTempHp: 0 })).toBeNull();
    expect(normalizeHpUpdated({ characterId: "c-1", newHp: 5 })).toBeNull();
  });

  it("fills cosmetic fields with safe defaults when they are missing", () => {
    const event = normalizeHpUpdated({ characterId: "c-1", newHp: 0, newTempHp: 0 });

    expect(event).not.toBeNull();
    expect(event?.characterId).toBe("c-1");
    expect(event?.newHp).toBe(0);
    expect(event?.droppedToZero).toBe(false);
    expect(event?.isInstantDeath).toBe(false);
    expect(typeof event?.previousHp).toBe("number");
    expect(typeof event?.previousTempHp).toBe("number");
  });
});

describe("applyHpUpdate", () => {
  it("writes the new hp/temp hp onto the cached detail", () => {
    const detail = makeDetail({ currentHp: 20, currentTempHp: 3 });

    const next = applyHpUpdate(detail, {
      characterId: "c-1",
      previousHp: 20,
      newHp: 8,
      previousTempHp: 3,
      newTempHp: 0,
      droppedToZero: false,
      isInstantDeath: false,
    });

    expect(next?.currentHp).toBe(8);
    expect(next?.currentTempHp).toBe(0);
    // ฟิลด์อื่นต้องไม่ถูกแตะ — broadcast ส่งมาแค่ HP ไม่ใช่ตัวละครทั้งใบ
    expect(next?.maxHp).toBe(24);
    expect(next?.armorClass).toBe(15);
    expect(next?.name).toBe("Aria");
  });

  it("ignores an event for a different character (returns the same reference)", () => {
    const detail = makeDetail({ id: "c-1", currentHp: 20 });

    const next = applyHpUpdate(detail, {
      characterId: "c-999",
      previousHp: 30,
      newHp: 1,
      previousTempHp: 0,
      newTempHp: 0,
      droppedToZero: false,
      isInstantDeath: false,
    });

    expect(next).toBe(detail);
  });

  it("bails out when there is nothing cached yet", () => {
    expect(
      applyHpUpdate(undefined, {
        characterId: "c-1",
        previousHp: 20,
        newHp: 8,
        previousTempHp: 0,
        newTempHp: 0,
        droppedToZero: false,
        isInstantDeath: false,
      }),
    ).toBeUndefined();
  });
});

describe("ownedCharacters", () => {
  it("keeps only the characters owned by the logged-in user", () => {
    const list = [
      makeSummary({ id: "c-1", ownerUserId: "u-1" }),
      makeSummary({ id: "c-2", ownerUserId: "u-2" }),
      makeSummary({ id: "c-3", ownerUserId: "u-1", controlledByDm: true }),
    ];

    expect(ownedCharacters(list, "u-1").map((c) => c.id)).toEqual(["c-1", "c-3"]);
  });

  it("returns nothing when the list or the user id is missing", () => {
    expect(ownedCharacters(undefined, "u-1")).toEqual([]);
    // ไม่รู้ว่าเป็นใคร = ไม่โชว์ตัวละครใครเลย ดีกว่าโชว์ทั้งแคมเปญ
    expect(ownedCharacters([makeSummary()], null)).toEqual([]);
  });
});

describe("attackTargets", () => {
  it("excludes the acting character from the target list", () => {
    const list = [
      makeSummary({ id: "c-1" }),
      makeSummary({ id: "c-2" }),
      makeSummary({ id: "c-3" }),
    ];

    expect(attackTargets(list, "c-2").map((c) => c.id)).toEqual(["c-1", "c-3"]);
  });

  it("returns every character when nobody is acting yet, and nothing when there is no list", () => {
    const list = [makeSummary({ id: "c-1" }), makeSummary({ id: "c-2" })];

    expect(attackTargets(list, null).map((c) => c.id)).toEqual(["c-1", "c-2"]);
    expect(attackTargets(undefined, "c-1")).toEqual([]);
  });
});

describe("describeCheckResult", () => {
  it("shows the rolled total against the dc on success", () => {
    const text = describeCheckResult(makeCheck({ total: 14, dc: 12, success: true }));

    expect(text).toContain("14");
    expect(text).toContain("12");
    expect(text).toContain("สำเร็จ");
    expect(text).not.toContain("ไม่สำเร็จ");
  });

  it("marks a failed check", () => {
    const text = describeCheckResult(makeCheck({ total: 7, dc: 15, success: false }));

    expect(text).toContain("7");
    expect(text).toContain("15");
    expect(text).toContain("ไม่สำเร็จ");
  });
});

describe("describeAttackResult", () => {
  it("reports damage and the target's hp transition on a hit", () => {
    const text = describeAttackResult(makeAttack(), "Goblin");

    expect(text).toContain("Goblin");
    expect(text).toContain("18"); // ผลทอย attack roll
    expect(text).toContain("15"); // AC ของเป้าหมาย
    expect(text).toContain("7"); // ดาเมจรวม
    expect(text).toContain("12"); // HP ก่อน
    expect(text).toContain("5"); // HP หลัง
  });

  it("says nothing about damage on a miss (backend sends null for every damage field)", () => {
    const text = describeAttackResult(
      makeAttack({
        outcome: "Miss",
        hit: false,
        damageComponents: null,
        totalDamage: null,
        // backend ส่ง null ทุกฟิลด์ที่เกี่ยวกับดาเมจ/HP ตอน miss (ดู AttackResolutionResponse — int?)
        targetPreviousHp: null,
        targetNewHp: null,
        targetPreviousTempHp: null,
        targetNewTempHp: null,
        targetDroppedToZero: null,
        targetIsInstantDeath: null,
      }),
      "Goblin",
    );

    expect(text).toContain("พลาด");
    expect(text).not.toContain("ดาเมจ");
  });

  it("calls out a critical hit distinctly from a normal hit", () => {
    const crit = describeAttackResult(makeAttack({ outcome: "CriticalHit" }), "Goblin");
    const normal = describeAttackResult(makeAttack({ outcome: "Hit" }), "Goblin");

    expect(crit).toContain("คริ");
    expect(normal).not.toContain("คริ");
  });

  it("calls out an automatic miss distinctly", () => {
    const text = describeAttackResult(
      makeAttack({
        outcome: "AutomaticMiss",
        hit: false,
        damageComponents: null,
        totalDamage: null,
        targetPreviousHp: null,
        targetNewHp: null,
        targetPreviousTempHp: null,
        targetNewTempHp: null,
        targetDroppedToZero: null,
        targetIsInstantDeath: null,
      }),
      "Goblin",
    );

    expect(text).toContain("พลาด");
    expect(text).not.toContain("ดาเมจ");
  });

  it("flags a target dropping to zero and instant death", () => {
    const dropped = describeAttackResult(
      makeAttack({ targetNewHp: 0, totalDamage: 12, targetDroppedToZero: true }),
      "Goblin",
    );
    const instant = describeAttackResult(
      makeAttack({
        targetNewHp: 0,
        totalDamage: 40,
        targetDroppedToZero: true,
        targetIsInstantDeath: true,
      }),
      "Goblin",
    );

    expect(dropped).toContain("0");
    expect(dropped).not.toContain("ตายทันที");
    expect(instant).toContain("ตายทันที");
  });

  it("falls back to the target id when the name is unknown", () => {
    const text = describeAttackResult(makeAttack({ targetCharacterId: "c-2" }), null);

    expect(text).toContain("c-2");
  });
});

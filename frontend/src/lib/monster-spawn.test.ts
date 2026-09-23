import { describe, it, expect } from "vitest";
import { describeSpawned, parseSpawnCount } from "./monster-spawn";

describe("parseSpawnCount", () => {
  it("accepts whole numbers from 1 to 20", () => {
    expect(parseSpawnCount("1")).toBe(1);
    expect(parseSpawnCount(" 20 ")).toBe(20);
  });

  it("rejects out-of-range, fractional and non-numeric input instead of clamping", () => {
    for (const raw of ["0", "21", "-1", "2.5", "", "abc", "1e1"]) {
      expect(parseSpawnCount(raw)).toBeNull();
    }
  });
});

describe("describeSpawned", () => {
  it("names a single spawn as-is", () => {
    expect(describeSpawned(["Goblin #1"])).toBe("Goblin #1");
  });

  it("collapses a run of numbered spawns to a range", () => {
    expect(describeSpawned(["Goblin #4", "Goblin #5", "Goblin #6"])).toBe("Goblin #4 – #6");
  });

  it("handles empty input and names without a number", () => {
    expect(describeSpawned([])).toBe("");
    expect(describeSpawned(["Ogre", "Ogre"])).toBe("Ogre และอีก 1 ตัว");
  });
});

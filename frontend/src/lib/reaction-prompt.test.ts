import { describe, it, expect } from "vitest";
import { isExpired, normalizeReactionPrompt, secondsRemaining, urgency } from "./reaction-prompt";

const validPayload = {
  reactionId: "11111111-1111-1111-1111-111111111111",
  sessionId: "22222222-2222-2222-2222-222222222222",
  moverCharacterId: "33333333-3333-3333-3333-333333333333",
  threatCharacterId: "44444444-4444-4444-4444-444444444444",
  threatName: "ก็อบลินยาม",
  timeoutSeconds: 30,
  deadlineUtc: "2026-09-10T10:00:30.000Z",
};

describe("normalizeReactionPrompt", () => {
  it("payload ครบคืน prompt ที่ใช้ได้", () => {
    expect(normalizeReactionPrompt(validPayload)).toEqual(validPayload);
  });

  it("ฟิลด์หายคืน null ไม่ใช่ prompt ที่กดแล้วพัง", () => {
    const { reactionId, ...missingId } = validPayload;
    expect(reactionId).toBeTruthy();
    expect(normalizeReactionPrompt(missingId)).toBeNull();
  });

  it("timeoutSeconds ที่มาเป็นสตริงถือว่าใช้ไม่ได้", () => {
    expect(normalizeReactionPrompt({ ...validPayload, timeoutSeconds: "30" })).toBeNull();
  });

  it("payload ที่ไม่ใช่ object คืน null ไม่ throw", () => {
    expect(normalizeReactionPrompt(null)).toBeNull();
    expect(normalizeReactionPrompt("prompt")).toBeNull();
    expect(normalizeReactionPrompt(undefined)).toBeNull();
  });
});

describe("secondsRemaining", () => {
  it("คิดจาก deadline ของ server ไม่ใช่นับถอยหลังเองจาก timeoutSeconds", () => {
    // ได้รับ event ช้าไป 10 วินาที — เวลาที่เหลือต้องเป็น 20 ไม่ใช่ 30
    const now = new Date("2026-09-10T10:00:10.000Z");
    expect(secondsRemaining(validPayload.deadlineUtc, now)).toBe(20);
  });

  it("เลยเวลาแล้วเป็น 0 ไม่ติดลบ", () => {
    const now = new Date("2026-09-10T10:01:00.000Z");
    expect(secondsRemaining(validPayload.deadlineUtc, now)).toBe(0);
  });

  it("ปัดขึ้น เพื่อให้วินาทีสุดท้ายยังแสดงเป็น 1 ไม่ใช่ 0 ทั้งที่ยังกดได้", () => {
    const now = new Date("2026-09-10T10:00:29.500Z");
    expect(secondsRemaining(validPayload.deadlineUtc, now)).toBe(1);
  });

  it("deadline ที่อ่านไม่ออกถือว่าหมดเวลา ไม่ใช่ NaN ขึ้นจอ", () => {
    expect(secondsRemaining("ไม่ใช่วันที่", new Date())).toBe(0);
  });
});

describe("isExpired", () => {
  it("ยังไม่ถึง deadline = ยังไม่หมดเวลา", () => {
    expect(isExpired(validPayload.deadlineUtc, new Date("2026-09-10T10:00:29.000Z"))).toBe(false);
  });

  it("ถึง deadline พอดีถือว่าหมดแล้ว", () => {
    expect(isExpired(validPayload.deadlineUtc, new Date("2026-09-10T10:00:30.000Z"))).toBe(true);
  });
});

describe("urgency", () => {
  it("คิดเป็นสัดส่วนของเวลาที่ DM ตั้งไว้ ไม่ใช่วินาทีคงที่", () => {
    // เหลือ 5 วินาทีเท่ากัน แต่คนละความหมายเมื่อ timeout ต่างกัน
    expect(urgency(5, 10)).toBe("hurry");
    expect(urgency(5, 300)).toBe("critical");
  });

  it("เพิ่งเริ่มนับ = ยังไม่ต้องเร่ง", () => {
    expect(urgency(30, 30)).toBe("calm");
  });

  it("timeout เป็น 0 ถือว่าวิกฤตทันที ไม่หารด้วยศูนย์", () => {
    expect(urgency(0, 0)).toBe("critical");
  });
});

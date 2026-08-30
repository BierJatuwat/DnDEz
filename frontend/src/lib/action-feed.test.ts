import { describe, it, expect } from "vitest";
import { normalizeActionMessageCreated, appendActionMessage, type ActionMessage } from "./action-feed";

describe("normalizeActionMessageCreated", () => {
  it("normalizes a well-formed payload", () => {
    const result = normalizeActionMessageCreated({
      messageId: "m-1",
      sessionId: "s-1",
      authorType: "Player",
      characterId: "c-1",
      content: "I search the room for traps.",
      createdAt: "2026-08-28T00:00:00Z",
    });

    expect(result).toEqual({
      id: "m-1",
      sessionId: "s-1",
      authorType: "Player",
      characterId: "c-1",
      content: "I search the room for traps.",
      createdAt: "2026-08-28T00:00:00Z",
    });
  });

  it("defaults characterId to null when absent (AI narration ไม่ผูก character)", () => {
    const result = normalizeActionMessageCreated({
      messageId: "m-2",
      sessionId: "s-1",
      authorType: "Ai",
      content: "The room is quiet.",
    });

    expect(result?.characterId).toBeNull();
  });

  it.each([
    ["not an object", "just a string"],
    ["null", null],
    ["missing messageId", { sessionId: "s-1", authorType: "Player", content: "x" }],
    ["missing sessionId", { messageId: "m-1", authorType: "Player", content: "x" }],
    ["missing authorType", { messageId: "m-1", sessionId: "s-1", content: "x" }],
    ["missing content", { messageId: "m-1", sessionId: "s-1", authorType: "Player" }],
    ["wrong-typed messageId", { messageId: 1, sessionId: "s-1", authorType: "Player", content: "x" }],
  ])("rejects a malformed payload: %s", (_label, payload) => {
    expect(normalizeActionMessageCreated(payload)).toBeNull();
  });
});

describe("appendActionMessage", () => {
  const message = (id: string): ActionMessage => ({
    id,
    sessionId: "s-1",
    authorType: "Player",
    characterId: "c-1",
    content: `content ${id}`,
    createdAt: "2026-08-28T00:00:00Z",
  });

  it("appends to the end (oldest -> newest)", () => {
    const result = appendActionMessage([message("m-1")], message("m-2"));
    expect(result.map((m) => m.id)).toEqual(["m-1", "m-2"]);
  });

  it("dedupes by id and returns the same array contents when the id already exists", () => {
    const existing = [message("m-1")];
    const result = appendActionMessage(existing, message("m-1"));
    expect(result).toEqual(existing);
  });

  it("caps the feed at 50 entries, keeping the newest", () => {
    let feed: ActionMessage[] = [];
    for (let i = 0; i < 55; i++) {
      feed = appendActionMessage(feed, message(`m-${i}`));
    }

    expect(feed).toHaveLength(50);
    expect(feed[0].id).toBe("m-5");
    expect(feed[49].id).toBe("m-54");
  });
});

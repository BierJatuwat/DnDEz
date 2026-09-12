import { describe, it, expect } from "vitest";
import { DEFAULT_SCENE_GRID, parseSceneGrid } from "./scene-grid";

describe("parseSceneGrid", () => {
  it("แกะค่าที่ DM ตั้งไว้จริงได้ครบ", () => {
    expect(parseSceneGrid('{"pixelsPerSquare":80,"offsetX":12,"offsetY":-4}')).toEqual({
      pixelsPerSquare: 80,
      offsetX: 12,
      offsetY: -4,
    });
  });

  it("ฉากที่ยังไม่ได้ตั้งกริดเลย ('{}' คือค่าเริ่มต้นของคอลัมน์) ได้ดีฟอลต์", () => {
    expect(parseSceneGrid("{}")).toEqual(DEFAULT_SCENE_GRID);
  });

  it("null/undefined/สตริงว่างได้ดีฟอลต์ ไม่ throw", () => {
    expect(parseSceneGrid(null)).toEqual(DEFAULT_SCENE_GRID);
    expect(parseSceneGrid(undefined)).toEqual(DEFAULT_SCENE_GRID);
    expect(parseSceneGrid("")).toEqual(DEFAULT_SCENE_GRID);
  });

  it("JSON ที่พังได้ดีฟอลต์ ไม่ throw", () => {
    expect(parseSceneGrid("{not valid json")).toEqual(DEFAULT_SCENE_GRID);
  });

  it("JSON ที่ถูกไวยากรณ์แต่ไม่ใช่ object (array/ตัวเลข/สตริง) ได้ดีฟอลต์", () => {
    expect(parseSceneGrid("[1,2,3]")).toEqual(DEFAULT_SCENE_GRID);
    expect(parseSceneGrid("42")).toEqual(DEFAULT_SCENE_GRID);
    expect(parseSceneGrid('"hello"')).toEqual(DEFAULT_SCENE_GRID);
    expect(parseSceneGrid("null")).toEqual(DEFAULT_SCENE_GRID);
  });

  it("pixelsPerSquare หายไปได้ดีฟอลต์เฉพาะฟิลด์นั้น", () => {
    expect(parseSceneGrid('{"offsetX":5,"offsetY":5}')).toEqual({
      pixelsPerSquare: DEFAULT_SCENE_GRID.pixelsPerSquare,
      offsetX: 5,
      offsetY: 5,
    });
  });

  it("pixelsPerSquare เป็น 0 หรือติดลบถือว่าใช้ไม่ได้ ตกไปใช้ดีฟอลต์ (กันหารด้วยศูนย์/พลิกภาพ)", () => {
    expect(parseSceneGrid('{"pixelsPerSquare":0}').pixelsPerSquare).toBe(
      DEFAULT_SCENE_GRID.pixelsPerSquare,
    );
    expect(parseSceneGrid('{"pixelsPerSquare":-10}').pixelsPerSquare).toBe(
      DEFAULT_SCENE_GRID.pixelsPerSquare,
    );
  });

  it("pixelsPerSquare เป็นสตริงแทนตัวเลข (jsonb แก้มือได้) ถือว่าใช้ไม่ได้", () => {
    expect(parseSceneGrid('{"pixelsPerSquare":"80"}').pixelsPerSquare).toBe(
      DEFAULT_SCENE_GRID.pixelsPerSquare,
    );
  });

  it("pixelsPerSquare เป็น NaN/Infinity ถือว่าใช้ไม่ได้", () => {
    expect(parseSceneGrid('{"pixelsPerSquare":null}').pixelsPerSquare).toBe(
      DEFAULT_SCENE_GRID.pixelsPerSquare,
    );
  });

  it("offsetX/offsetY หายไปได้ดีฟอลต์ 0 โดยไม่กระทบ pixelsPerSquare ที่แกะได้", () => {
    expect(parseSceneGrid('{"pixelsPerSquare":100}')).toEqual({
      pixelsPerSquare: 100,
      offsetX: 0,
      offsetY: 0,
    });
  });
});

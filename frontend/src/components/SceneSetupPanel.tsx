"use client";

import { useRef, useState } from "react";
import { useSceneSetup, uploadErrorMessage } from "@/lib/use-scene-setup";
import { parseSceneGrid } from "@/lib/scene-grid";
import type { Scene } from "@/lib/scene-types";

const inputClass =
  "rounded border border-black/20 dark:border-white/20 bg-transparent px-2 py-1 text-sm";
const primaryBtn = "rounded bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-50";

/**
 * Sprint H Part 3 — ตั้งฉากให้พร้อมเล่น: อัปโหลดแผนที่ → สร้างฉาก → ตั้งกริดให้ตรงตารางบนรูป
 *
 * เจอตอนเดิน onboarding ด้วยมือจริงว่าเส้นทางนี้ **ไม่มี UI เลย** ทั้งที่ endpoint พร้อมมาตั้งแต่
 * Sprint G Part 4 — DM เห็นข้อความบอกให้ "อัปโหลดแผนที่แล้วสร้างฉากก่อน" แต่ไม่มีปุ่มให้กด
 * กลายเป็นทางตันที่ทำให้เล่นจริงไม่ได้เลยแม้ระบบอื่นจะพร้อมหมดแล้ว
 *
 * **รวมอัปโหลด+สร้างฉากเป็นปุ่มเดียว** — ในมุมของ DM มันคือ "เอาแผนที่ขึ้นโต๊ะ" ก้าวเดียว
 * การแยกเป็นสองปุ่ม (อัปโหลดไฟล์ แล้วค่อยสร้างฉากที่อ้างไฟล์) เป็นรูปร่างของ API ไม่ใช่ของงาน
 */
export function SceneSetupPanel({
  campaignId,
  sessionId,
  scene,
}: {
  campaignId: string;
  sessionId: string;
  scene: Scene | null;
}) {
  const { createSceneWithMap, setGrid } = useSceneSetup(campaignId, sessionId);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const [sceneName, setSceneName] = useState("ฉากแรก");
  const [fileName, setFileName] = useState<string | null>(null);

  const grid = parseSceneGrid(scene?.grid);
  const [pps, setPps] = useState(String(grid.pixelsPerSquare));
  const [offsetX, setOffsetX] = useState(String(grid.offsetX));
  const [offsetY, setOffsetY] = useState(String(grid.offsetY));

  const submitMap = () => {
    const file = fileRef.current?.files?.[0];
    if (!file || !sceneName.trim()) return;
    createSceneWithMap.mutate({ file, name: sceneName.trim() });
  };

  if (!scene) {
    return (
      <div className="space-y-3 rounded border border-dashed border-black/20 dark:border-white/20 p-4">
        <div>
          <h3 className="font-medium">เอาแผนที่ขึ้นโต๊ะ</h3>
          <p className="text-xs opacity-60">
            เลือกไฟล์รูปแผนที่ ตั้งชื่อฉาก แล้วกดปุ่มเดียวจบ — หลังจากนั้นค่อยจัดกริดให้ตรงตาราง
          </p>
        </div>

        <input
          ref={fileRef}
          type="file"
          accept="image/png,image/jpeg,image/webp,image/gif"
          onChange={(e) => setFileName(e.target.files?.[0]?.name ?? null)}
          className="block w-full text-sm file:mr-3 file:rounded file:border-0 file:bg-indigo-600 file:px-3 file:py-1.5 file:text-white"
          aria-label="ไฟล์แผนที่"
        />

        <label className="block text-sm">
          <span className="mr-2 opacity-60">ชื่อฉาก</span>
          <input
            value={sceneName}
            onChange={(e) => setSceneName(e.target.value)}
            className={inputClass}
            aria-label="ชื่อฉาก"
          />
        </label>

        <button
          type="button"
          onClick={submitMap}
          disabled={!fileName || !sceneName.trim() || createSceneWithMap.isPending}
          className={primaryBtn}
        >
          {createSceneWithMap.isPending ? "กำลังอัปโหลด..." : "อัปโหลดแล้วสร้างฉาก"}
        </button>

        {createSceneWithMap.isError && (
          <p className="text-xs text-red-500">{uploadErrorMessage(createSceneWithMap.error)}</p>
        )}
      </div>
    );
  }

  return (
    <div className="space-y-2 rounded border border-black/10 dark:border-white/10 p-3">
      <div>
        <h3 className="text-sm font-medium">จัดกริดให้ตรงตารางบนรูป</h3>
        {/* บอกวิธีวัดให้ชัด เพราะ DM ที่ไม่เคยใช้ VTT มาก่อนจะไม่รู้ว่า "พิกเซลต่อช่อง" หามาจากไหน */}
        <p className="text-xs opacity-60">
          ใส่ความกว้างของ 1 ช่องบนรูปเป็นพิกเซล — ถ้าตารางบนรูปห่างกัน 64 พิกเซล ก็ใส่ 64
          · token จะวางตรงช่องพอดีเมื่อค่านี้ถูก
        </p>
      </div>

      <div className="flex flex-wrap items-end gap-2">
        <label className="text-sm">
          <span className="mr-1 opacity-60">พิกเซล/ช่อง</span>
          <input
            type="number"
            value={pps}
            onChange={(e) => setPps(e.target.value)}
            className={`w-24 ${inputClass}`}
            aria-label="พิกเซลต่อช่อง"
          />
        </label>
        <label className="text-sm">
          <span className="mr-1 opacity-60">เลื่อน x</span>
          <input
            type="number"
            value={offsetX}
            onChange={(e) => setOffsetX(e.target.value)}
            className={`w-20 ${inputClass}`}
            aria-label="ออฟเซ็ต x"
          />
        </label>
        <label className="text-sm">
          <span className="mr-1 opacity-60">y</span>
          <input
            type="number"
            value={offsetY}
            onChange={(e) => setOffsetY(e.target.value)}
            className={`w-20 ${inputClass}`}
            aria-label="ออฟเซ็ต y"
          />
        </label>
        <button
          type="button"
          onClick={() =>
            setGrid.mutate({
              sceneId: scene.id,
              pixelsPerSquare: Number(pps) || 0,
              offsetX: Number(offsetX) || 0,
              offsetY: Number(offsetY) || 0,
            })
          }
          disabled={setGrid.isPending || Number(pps) <= 0}
          className={primaryBtn}
        >
          บันทึกกริด
        </button>
      </div>

      {setGrid.isError && <p className="text-xs text-red-500">บันทึกกริดไม่สำเร็จ</p>}
    </div>
  );
}

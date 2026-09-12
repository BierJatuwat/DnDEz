import { legendItems, type LegendItem, type SceneTerrain } from "@/lib/terrain-paint";

/**
 * Sprint H Part 6 — คำอธิบายสัญลักษณ์พื้นใต้แผนที่ (novice check: ผู้เล่นต้องรู้เองจากจอว่าตรงนั้นเดินยาก
 * โดยไม่ต้องมีใครบอก) · สีของตัวอย่างต้องตรงกับที่ `SceneMap` วาดจริง ไม่งั้นคำอธิบายจะชี้ผิดของ
 */
export function TerrainLegend({ terrain }: { terrain: SceneTerrain }) {
  const items = legendItems(terrain);
  if (items.length === 0) return null;

  return (
    <ul className="space-y-1 text-xs" aria-label="คำอธิบายพื้นบนแผนที่">
      {items.map((item) => (
        <li key={item.kind} className="flex items-center gap-2">
          <Swatch kind={item.kind} />
          <span>{item.text}</span>
        </li>
      ))}
    </ul>
  );
}

function Swatch({ kind }: { kind: LegendItem["kind"] }) {
  const style =
    kind === "difficult"
      ? {
          background:
            "repeating-linear-gradient(45deg, #f59e0b 0 2px, rgba(120, 53, 15, 0.45) 2px 6px)",
          border: "1px solid #b45309",
        }
      : { background: "#57534e", border: "1px solid #1c1917" };

  return <span aria-hidden="true" className="inline-block h-4 w-4 shrink-0 rounded-sm" style={style} />;
}

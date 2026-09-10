/**
 * Sprint G Part 7 — type ที่ต้องตรงกับ `PresetListItem` / `CharacterCreatedResponse` ฝั่ง C# เป๊ะ
 * (`src/DnDEz.Api/Endpoints/CharacterPresetEndpoints.cs`, `CharacterEndpoints.cs`) ไม่ได้ generate
 * อัตโนมัติ แก้ฝั่งไหนต้องมาแก้ที่นี่ด้วย
 */
export type PresetAbilityScores = {
  strength: number;
  dexterity: number;
  constitution: number;
  intelligence: number;
  wisdom: number;
  charisma: number;
};

export type CharacterPreset = {
  id: string;
  name: string;
  scope: "Global" | "Campaign" | string;
  source: "Authored" | "Harvested" | string;
  level: number;
  popularityCount: number;
  race: string | null;
  class: string | null;
  armorClass: number | null;
  hitPoints: number | null;
  abilityScores: PresetAbilityScores | null;
};

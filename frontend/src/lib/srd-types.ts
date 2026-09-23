/**
 * คลัง SRD 5.2.1 — type ที่ต้องตรงกับ response ฝั่ง C# เป๊ะ (`src/DnDEz.Api/Endpoints/SrdEndpoints.cs`,
 * `MonsterEndpoints.cs`) ไม่ได้ generate อัตโนมัติ แก้ฝั่งไหนต้องมาแก้ที่นี่ด้วย
 *
 * ⚠️ `/srd/*` เปิดให้ทุกคนที่ล็อกอินอ่าน (เนื้อหา CC-BY สาธารณะ ไม่ใช่ความลับของแคมเปญ) แต่
 * `GET /campaigns/{id}/monsters` เป็น **DM-only** — AC/HP ของศัตรูในแคมเปญคือของที่ผู้เล่นต้องค้นเอาในเกม
 */

/** สรุปหนึ่งแถวในลิสต์คลัง — ไม่มี stat block เต็ม (ต้องกด GET รายตัว) */
export type SrdMonsterSummary = {
  key: string;
  name: string;
  size: string;
  type: string;
  alignment: string;
  armorClass: number;
  hitPoints: number;
  speedFeet: number;
  reachFeet: number;
  /** "1/8" "1/4" "1/2" หรือจำนวนเต็ม — เป็นข้อความเสมอ ห้ามแปลงเป็นตัวเลขแล้วเก็บกลับ */
  challengeRating: string;
  xp: number;
};

export type SrdListResponse<T> = {
  total: number;
  items: T[];
  attribution: string;
};

/** ท่า/คุณสมบัติหนึ่งรายการใน stat block */
export type SrdMonsterAction = {
  kind: "Trait" | "Action" | "BonusAction" | "Reaction" | "LegendaryAction" | string;
  name: string;
  text: string;
};

/** ค่าความสามารถหนึ่งช่อง · `sourceFixup` = จุดที่ต้นฉบับ PDF พิมพ์ผิดแล้วตัวนำเข้าแก้ให้ (มีน้อยมาก) */
export type SrdAbility = { score: number; mod: number; save: number; sourceFixup?: string };

export type SrdMonsterDetail = SrdMonsterSummary & {
  sourceVersion: string;
  data: {
    abilities: Record<string, SrdAbility>;
    speedText?: string | null;
    hpText?: string | null;
    hitDice?: string | null;
    skills?: string | null;
    senses?: string | null;
    languages?: string | null;
    resistances?: string | null;
    immunities?: string | null;
    vulnerabilities?: string | null;
    gear?: string | null;
    traits?: SrdMonsterAction[];
    actions?: SrdMonsterAction[];
    bonusActions?: SrdMonsterAction[];
    reactions?: SrdMonsterAction[];
    legendaryActions?: SrdMonsterAction[];
  };
  /**
   * ปุ่มสลับภาษา (2026-09-22) — มีเฉพาะตอนขอ `?lang=th` เท่านั้น (ไม่ส่งมาตอนขอ `lang=en`/ไม่ส่ง `lang`
   * เลย เพราะยังไม่มีความหมาย)
   *
   * ⚠️ **ชื่อมอนสเตอร์ไม่แปล** — เป็น "ชื่อเฉพาะ" ตามคำขอผู้ใช้ คงเป็นอังกฤษเสมอไม่ว่า `lang` จะเป็นอะไร
   * `translated` จึงหมายถึงเฉพาะ **ขนาด/ชนิด/แนวคิด** แปลแล้วไหม (330/330 ตัวจริง แต่เขียนเป็น field ไว้
   * ไม่ผูกกับตัวเลขคงที่) `contentTranslated` = traits/actions แปลเต็มหรือยังเป็นอังกฤษ (fallback) —
   * ใช้แยกสองเคสนี้ในหน้าจอ เพราะขนาด/ชนิดแปลแล้วไม่ได้แปลว่าเนื้อหาข้างในแปลด้วย
   */
  translated?: boolean | null;
  contentTranslated?: boolean | null;
  attribution: string;
};

/** หมวดของ `rules_corpus_entries` ที่ import มา — เวท / สถานะ / กฎที่มีป้ายกำกับ */
export type SrdCorpusCategory = "spell" | "condition" | "rule";

export type SrdCorpusItem = {
  category: SrdCorpusCategory | string;
  key: string;
  title: string;
  /** โครงสร้างของรายการนั้น — เวทมี level/school/classes/... · กฎมี `{tag}` · สถานะมี `{tag:"Condition"}` */
  data: {
    level?: number;
    school?: string;
    classes?: string[];
    castingTime?: string;
    range?: string;
    components?: string;
    duration?: string;
    tag?: string;
  };
  /** มีเฉพาะตอน `?lang=th` — เนื้อหา (content) ที่ร้องขอมาแปลแล้วจริงไหม (ดู `SrdMonsterDetail.contentTranslated`) */
  contentTranslated?: boolean | null;
};

export type SrdCorpusDetail = SrdCorpusItem & {
  content: string;
  license: string;
  source: string;
  /**
   * มีเฉพาะตอน `?lang=th` — ⚠️ **ไม่ครอบคลุมชื่อ/หัวข้อ (title)** ชื่อเวท/condition/rule เป็น "ชื่อเฉพาะ"
   * ตามคำขอผู้ใช้ (2026-09-22) คงเป็นอังกฤษเสมอ — flag นี้หมายถึง school/classes (เฉพาะเวท) แปลแล้วไหม
   */
  translated?: boolean | null;
  attribution: string;
};

/** แม่แบบมอนสเตอร์ที่อยู่ในแคมเปญนี้แล้ว (คัดลอกมาจากคลัง หรือ DM ใส่เองผ่าน DB) */
export type CampaignMonster = {
  id: string;
  name: string;
  armorClass: number;
  hitPoints: number;
  speedFeet: number;
  reachFeet: number;
  /** null = ไม่ได้มาจากคลัง SRD */
  srdKey: string | null;
  reviewStatus: string;
};

export type CopySrdMonsterResult = {
  monsterId: string;
  srdKey: string;
  name: string;
  /** false = มีอยู่แล้วในแคมเปญ คืนตัวเดิมให้ ไม่ได้สร้างซ้ำ */
  created: boolean;
};

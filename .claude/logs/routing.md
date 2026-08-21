# Routing Log

บันทึกทุกครั้งที่มีการ escalate, dispatch (review), หรือ fallback เกิดขึ้น เพื่อใช้จูนเกณฑ์ใน CLAUDE.md ภายหลัง

รูปแบบ: `[timestamp] [agent ปัจจุบัน] -> [agent ที่ escalate/dispatch ไป] : เหตุผล`

---
[2026-08-21 10:23 UTC] sonnet-worker -> sonnet-worker (self, no escalation) : Phase 1 rules engine (dice parser/advantage/IDiceRoller/DmEntered/guard rails) เป็นงานขอบเขตชัด สเปกล็อกแล้วในพรอมต์ owner ไม่ต้องวางโครง/แตก subtask ข้ามชั้น ทำเองในฐานะ first triage ได้
[2026-08-21 10:31 UTC] sonnet-worker -> sonnet-worker (dispatch, TDD green phase) : เทสต์ 90 เคส red แล้ว (compile ผ่าน, fail ด้วย NotImplementedException ตามคาด) ตาม TDD flow ข้อ 4 ผู้เขียนเทสต์ห้ามเขียน implementation เอง ต้อง dispatch งาน green ออกไปให้ worker อื่น เลือก sonnet (ไม่ใช่ haiku) เพราะ parser grammar + guard rail + advantage/kh-kl + seed replay มี edge case และการตัดสินใจเชิง logic เยอะเกินระดับ haiku
[2026-08-21 10:43 UTC] main -> code-reviewer : Phase 1 green แล้ว (90/90 passed จริง) Code Review Flow ข้อ 1 บังคับให้ dispatch reviewer ก่อนรวมผล · reviewer ระดับเดียวกับผู้เขียน (sonnet) ตามข้อ 2
[2026-08-21 10:43 UTC] main -> security-reviewer : เข้า trigger 3 ข้อ — parser รับ string จากภายนอก (DoS), seed เป็น anti-cheat boundary (เก็บลง dice_rolls.seed ถ้าเดาได้ผู้เล่นทำนายผลทอยได้), เพิ่ม dependency ใหม่ (xUnit)
[2026-08-21 10:45 UTC] code-reviewer -> security-reviewer : DiceExpressionParser.cs รับ input ดิบจากภายนอก (dice notation ที่ผู้ใช้พิมพ์) ผ่าน regex + long parsing พร้อม guard rail กัน DoS (MaxExpressionLength/MaxDiceCountPerGroup/MaxSides ฯลฯ) — เข้าเงื่อนไข trigger "รับ input จากภายนอก" ตาม CLAUDE.md ขอให้ security-reviewer ตรวจ ReDoS/integer-overflow-guard เพิ่มเติมเป็นตาข่ายรอง (ไม่ใช่ blocker จาก code-reviewer แต่เข้าเงื่อนไขต้อง flag)
[2026-08-21 10:54 UTC] main -> sonnet-worker : review follow-up — ผมเขียนเทสต์ ReviewFollowUpTests.cs เอง (red 5 เคส Unicode digit) TDD flow ข้อ 4 ห้ามคนเขียนเทสต์เขียน implementation เอง จึง dispatch การแก้ DiceExpressionParser ออกไป

using System.Text.Json;

namespace DnDEz.Api.Orchestration;

/// <summary>
/// Sprint D Part 2/3 — เดินสายเรียก Anthropic API ด้วยคีย์ BYOK ของผู้ใช้เอง (แยก interface ออกจาก
/// implementation จริงโดยตั้งใจ เพราะนี่คือ external third-party paid API — ต่างจาก dice/Postgres/Redis
/// ของโปรเจกต์นี้เองที่มีกฎ "ห้าม mock" การเรียก HTTP จริงไปหา Anthropic ในเทสต์อัตโนมัติทำไม่ได้
/// อยู่แล้ว (ไม่มี key จริงให้ใช้, ค่าใช้จ่ายจริง, ผลไม่ deterministic) จึง fake ที่ boundary นี้แทน —
/// pattern มาตรฐานสำหรับ external HTTP dependency ไม่ใช่การ mock กลไกภายในของระบบนี้เอง)
///
/// ⚠️ Part 3 เปลี่ยนจาก Part 2 เดิม (`SendWithSingleToolRoundTripAsync` ตัวเดียวที่ auto-execute tool
/// ให้เสร็จในตัว) มาแยกเป็น 2 เมธอด เพราะ **approval gate (Suggest level) ต้องหยุดรอระหว่าง "ได้ยินว่า
/// Claude อยากเรียก tool" กับ "tool ถูก execute จริง"** — ช่วงรอนี้อาจกินเวลาข้าม HTTP request (DM
/// ต้องกด approve ทีหลัง) จึงต้องมีจุดตัดที่ endpoint หยุดแล้ว persist state ไว้ก่อนได้ (ดู `Proposal`
/// entity + `ConsultEndpoints.MapConsultEndpoints`)
/// </summary>
public interface IAnthropicClient
{
    /// <summary>ยิง request เบา ๆ ไปเช็คว่า key ใช้งานได้จริงไหม — เรียกตอน PUT /users/me/api-keys</summary>
    Task<ApiKeyValidationOutcome> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// ส่ง conversation history + tool definitions (ถ้ามี) ไปให้ Claude "หนึ่งเทิร์น" **ไม่ execute
    /// tool ใด ๆ เองเด็ดขาด** แค่รายงานกลับว่า Claude ตอบ narration เฉย ๆ หรืออยากเรียก tool —
    /// การตัดสินใจว่าจะ auto-execute ทันที (Autonomous) หรือรอ approve ก่อน (Suggest) เป็นหน้าที่ของ
    /// endpoint ไม่ใช่ client ชั้นนี้ · <paramref name="tools"/> ว่างได้ (Manual level สำหรับ tool ที่
    /// เขียน state — ดู `OrchestratorTurnRunner` ว่า tool ไหนยังเสนอแม้ Manual)
    ///
    /// **2026-09-22 (Part B) — เปลี่ยนจาก tool เดียว (`AnthropicToolDefinition? tool`) เป็นลิสต์**
    /// เพราะตอนนี้มี tool มากกว่าหนึ่งที่อาจเสนอพร้อมกันได้ (roll_dice ที่เขียน state + lookup_rule/
    /// lookup_monster ที่อ่านอย่างเดียว) — ยังคงข้อจำกัดเดิม "1 tool call ต่อเทิร์น" (ดู
    /// `AnthropicClient.ExtractTurnResult`) แค่ให้ Claude เลือกได้จากหลายตัว ไม่ใช่ประกาศ agentic loop
    /// เต็มรูป
    ///
    /// <paramref name="system"/> — system prompt (2026-09-22, แก้บั๊ก "AI ไม่รู้ context ของ session
    /// เลย" ที่ผู้ใช้ถาม) null/ว่าง = ไม่ส่ง field `system` ไปเลย (ไม่ใช่ส่ง string ว่าง) ผู้เรียกเป็น
    /// คนตัดสินใจว่าจะใส่อะไร — client ชั้นนี้แค่ส่งต่อ ไม่ประกอบเนื้อหาเอง (ดู
    /// `OrchestratorTurnRunner.BuildSystemPromptAsync` สำหรับ logic ประกอบจริง และเหตุผลว่าทำไม
    /// `StoryBeat.DmNotes`/`Secrets` ไม่เคยอยู่ใน prompt ที่ผ่าน method นี้)
    /// </summary>
    Task<AnthropicTurnResult> SendTurnAsync(
        string apiKey,
        string? system,
        IReadOnlyList<AnthropicHistoryMessage> history,
        IReadOnlyList<AnthropicToolDefinition> tools,
        CancellationToken cancellationToken);

    /// <summary>
    /// ส่งต่อบทสนทนาหลังจาก tool ถูก execute แล้วจริง (ไม่ว่าจะ auto ทันทีหรือรอ DM approve ก่อนก็ตาม)
    /// — <paramref name="rawAssistantContent"/> ต้องเป็นค่าเดียวกับ <see cref="AnthropicTurnResult.RawAssistantContent"/>
    /// ที่ได้จาก <see cref="SendTurnAsync"/> รอบก่อนหน้าเป๊ะ ๆ (Anthropic ต้องการ echo content array
    /// ของ assistant message รอบแรกกลับไปตรง ๆ ไม่ใช่สร้างใหม่เอง) — เก็บเป็น jsonb ใน `Proposal`
    /// ระหว่างรอ approve ได้เพราะเป็น `JsonElement` ธรรมดา serialize/deserialize ได้ตรงไปตรงมา
    ///
    /// <paramref name="system"/> — ต้องส่งค่า**เดียวกับที่ใช้ตอน <see cref="SendTurnAsync"/> รอบแรก**
    /// (Anthropic ไม่บังคับ แต่ระบบนี้ต้องการให้บทสนทนาทั้งรอบเห็น context เดียวกันตลอด ไม่ใช่สลับ
    /// system prompt กลางบทสนทนา) · <paramref name="tools"/> ก็เช่นกัน ควรเป็นชุดเดียวกับรอบแรก
    /// </summary>
    Task<AnthropicTurnResult> ContinueWithToolResultAsync(
        string apiKey,
        string? system,
        IReadOnlyList<AnthropicHistoryMessage> history,
        IReadOnlyList<AnthropicToolDefinition> tools,
        JsonElement rawAssistantContent,
        string toolUseId,
        string toolResultText,
        CancellationToken cancellationToken);

    /// <summary>
    /// หนึ่งรอบของผู้ช่วย DM แบบทำงานหลายขั้น (`AssistantEndpoints`, 2026-09-23) — ต่างจาก
    /// <see cref="SendTurnAsync"/> สองข้อ: (1) <paramref name="messages"/> เป็น content blocks ดิบ
    /// (`tool_use`/`tool_result` ฝังอยู่ได้) ผู้เรียกเป็นเจ้าของบทสนทนาทั้งก้อนและต่อท้ายเองทีละรอบ
    /// (2) คืน tool_use **ทุกตัว** ในคำตอบ ไม่ใช่แค่ตัวแรก — Anthropic ต้องได้ tool_result ครบทุกตัวใน
    /// ข้อความถัดไป ไม่งั้นปฏิเสธ request
    /// </summary>
    Task<AgentTurnResult> SendAgentTurnAsync(
        string apiKey,
        string? system,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AnthropicToolDefinition> tools,
        CancellationToken cancellationToken);
}

/// <summary>ข้อความหนึ่งรายการของบทสนทนาแบบหลายขั้น — <see cref="Content"/> เป็น string หรือ array ของ
/// content block ตาม Messages API (เก็บเป็น JsonElement เพื่อ serialize ลง `assistant_runs.state` ตรง ๆ)</summary>
public sealed record AgentMessage(string Role, JsonElement Content);

public sealed record AgentTurnResult(
    string Text, IReadOnlyList<AnthropicPendingToolUse> ToolUses, JsonElement RawAssistantContent);

public enum ApiKeyValidationOutcome
{
    Valid,
    Invalid,

    /// <summary>เช็คไม่สำเร็จเพราะเหตุผลที่ไม่เกี่ยวกับตัว key เอง (network, 5xx, timeout) — ห้ามตีความ
    /// เป็น Invalid เด็ดขาด ไม่งั้น Anthropic ล่มชั่วคราวจะทำให้ key ที่ใช้ได้จริงถูกตราว่าใช้ไม่ได้</summary>
    Unknown,
}

/// <summary>ข้อความในประวัติบทสนทนา — Role เป็น "user" หรือ "assistant" เท่านั้นตามที่ Anthropic Messages
/// API รองรับ (ชั้น endpoint เป็นคนแปลง MessageAuthorType ของระบบนี้เป็นสองค่านี้)</summary>
public sealed record AnthropicHistoryMessage(string Role, string Text);

public sealed record AnthropicToolDefinition(string Name, string Description, object InputSchema);

/// <summary>tool ที่ Claude ตัดสินใจอยากเรียกในเทิร์นนี้ — **ยังไม่ได้ execute จริง** (ดู doc comment
/// ของ interface)</summary>
public sealed record AnthropicPendingToolUse(string Id, string Name, JsonElement Input);

/// <summary>
/// ผลของหนึ่งเทิร์น — <see cref="ToolUse"/> เป็น null แปลว่า Claude ตอบ narration จบเลยไม่เรียก tool
/// <see cref="RawAssistantContent"/> ต้อง echo กลับเข้า <see cref="IAnthropicClient.ContinueWithToolResultAsync"/>
/// เป๊ะ ๆ ถ้าจะดำเนินการ tool call นี้ต่อ (ไม่ว่าจะทันทีหรือหลังรอ approve)
/// </summary>
public sealed record AnthropicTurnResult(string Text, AnthropicPendingToolUse? ToolUse, JsonElement RawAssistantContent);

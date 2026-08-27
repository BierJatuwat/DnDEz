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
    /// ส่ง conversation history + tool definition (ถ้ามี) ไปให้ Claude "หนึ่งเทิร์น" **ไม่ execute
    /// tool ใด ๆ เองเด็ดขาด** แค่รายงานกลับว่า Claude ตอบ narration เฉย ๆ หรืออยากเรียก tool —
    /// การตัดสินใจว่าจะ auto-execute ทันที (Autonomous) หรือรอ approve ก่อน (Suggest) เป็นหน้าที่ของ
    /// endpoint ไม่ใช่ client ชั้นนี้ · <paramref name="tool"/> เป็น null ได้ (Manual level — ไม่เสนอ
    /// tool ให้ Claude เลย เพราะ subsystem นั้นตั้งไว้ว่า "DM ตัดสินเอง")
    /// </summary>
    Task<AnthropicTurnResult> SendTurnAsync(
        string apiKey,
        IReadOnlyList<AnthropicHistoryMessage> history,
        AnthropicToolDefinition? tool,
        CancellationToken cancellationToken);

    /// <summary>
    /// ส่งต่อบทสนทนาหลังจาก tool ถูก execute แล้วจริง (ไม่ว่าจะ auto ทันทีหรือรอ DM approve ก่อนก็ตาม)
    /// — <paramref name="rawAssistantContent"/> ต้องเป็นค่าเดียวกับ <see cref="AnthropicTurnResult.RawAssistantContent"/>
    /// ที่ได้จาก <see cref="SendTurnAsync"/> รอบก่อนหน้าเป๊ะ ๆ (Anthropic ต้องการ echo content array
    /// ของ assistant message รอบแรกกลับไปตรง ๆ ไม่ใช่สร้างใหม่เอง) — เก็บเป็น jsonb ใน `Proposal`
    /// ระหว่างรอ approve ได้เพราะเป็น `JsonElement` ธรรมดา serialize/deserialize ได้ตรงไปตรงมา
    /// </summary>
    Task<AnthropicTurnResult> ContinueWithToolResultAsync(
        string apiKey,
        IReadOnlyList<AnthropicHistoryMessage> history,
        AnthropicToolDefinition tool,
        JsonElement rawAssistantContent,
        string toolUseId,
        string toolResultText,
        CancellationToken cancellationToken);
}

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

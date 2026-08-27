using System.Text.Json;

namespace DnDEz.Api.Orchestration;

/// <summary>
/// Sprint D Part 2 — เดินสายเรียก Anthropic API ด้วยคีย์ BYOK ของผู้ใช้เอง (แยก interface ออกจาก
/// implementation จริงโดยตั้งใจ เพราะนี่คือ external third-party paid API — ต่างจาก dice/Postgres/Redis
/// ของโปรเจกต์นี้เองที่มีกฎ "ห้าม mock" การเรียก HTTP จริงไปหา Anthropic ในเทสต์อัตโนมัติทำไม่ได้
/// อยู่แล้ว (ไม่มี key จริงให้ใช้, ค่าใช้จ่ายจริง, ผลไม่ deterministic) จึง fake ที่ boundary นี้แทน —
/// pattern มาตรฐานสำหรับ external HTTP dependency ไม่ใช่การ mock กลไกภายในของระบบนี้เอง)
/// </summary>
public interface IAnthropicClient
{
    /// <summary>ยิง request เบา ๆ ไปเช็คว่า key ใช้งานได้จริงไหม — เรียกตอน PUT /users/me/api-keys
    /// (Sprint D Part 1 เดิมเลื่อนมาไว้ Part 2 เพราะต้อง client นี้อยู่แล้ว)</summary>
    Task<ApiKeyValidationOutcome> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// ส่ง conversation history + tool definition เดียวไปให้ Claude แล้วรอผล — ถ้า Claude ตัดสินใจ
    /// เรียก tool (stop_reason=tool_use) จะเรียก <paramref name="executeTool"/> ให้อัตโนมัติทันที
    /// (Part 2 ยังไม่มี approval gate — auto-allow เสมอ, gate จริงเป็นของ Part 3) แล้วส่งผลกลับไปให้
    /// Claude รอบที่สองเพื่อเอา narration สุดท้าย **จำกัดแค่ 1 รอบ tool round-trip เท่านั้น** (ตรงกับ
    /// ขอบเขต "skeleton" ของ Part 2 — ยังไม่ใช่ agentic loop เต็มรูปที่วนได้หลายรอบ)
    /// </summary>
    Task<AnthropicConversationResult> SendWithSingleToolRoundTripAsync(
        string apiKey,
        IReadOnlyList<AnthropicHistoryMessage> history,
        AnthropicToolDefinition tool,
        Func<JsonElement, CancellationToken, Task<string>> executeTool,
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

public sealed record AnthropicToolCallRecord(string Name, JsonElement Input, string Result);

/// <summary>ToolCall เป็น null แปลว่า Claude ตอบ narration เลยโดยไม่เรียก tool รอบนี้</summary>
public sealed record AnthropicConversationResult(string FinalText, AnthropicToolCallRecord? ToolCall);

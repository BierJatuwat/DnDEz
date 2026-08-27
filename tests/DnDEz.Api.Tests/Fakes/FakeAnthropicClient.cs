using System.Text.Json;
using DnDEz.Api.Orchestration;

namespace DnDEz.Api.Tests.Fakes;

/// <summary>
/// Sprint D Part 2 — ห้ามเรียก Anthropic API จริงในเทสต์อัตโนมัติเด็ดขาด (ไม่มี key จริง, ไม่
/// deterministic, มีต้นทุนจริงเป็นเงิน) — fake ตัวนี้แทนที่ `IAnthropicClient` จริงผ่าน
/// `WebApplicationFactory.ConfigureServices` ในทุกเทสต์ที่แตะ endpoint ที่ inject มัน
///
/// พฤติกรรมกำหนดจากเนื้อหา input เอง ให้เทสต์ควบคุมผลลัพธ์ได้ตรง ๆ โดยไม่ต้อง mock framework ใด ๆ:
///  • `ValidateKeyAsync`: key ที่มีคำว่า "invalid" → Invalid, "unknown-outcome" → Unknown, อื่น ๆ → Valid
///  • `SendWithSingleToolRoundTripAsync`: ข้อความสุดท้ายมีคำว่า "roll" (ไม่สนตัวพิมพ์) → เรียก
///    tool `roll_dice` จริงผ่าน callback ที่รับมา (พิสูจน์ tool round-trip ทำงานจริง) ไม่งั้นตอบ
///    narration เฉย ๆ ไม่เรียก tool — ถ้ามีคำว่า "malformed-tool-input" ด้วย จะส่ง input ที่ไม่มี
///    field "expression" เลย (จำลอง input ผิดรูปแบบ) เพื่อพิสูจน์ endpoint executor จับ error
///    แล้วตอบกลับเป็น tool_result ข้อความ ไม่ใช่โยน exception จน request ทั้งก้อนล้ม
/// </summary>
public sealed class FakeAnthropicClient : IAnthropicClient
{
    /// <summary>เทสต์ inspect ได้ว่า history ที่ endpoint ส่งมาจริงมีอะไรบ้าง (สร้าง message
    /// history จาก DB ถูกต้องไหม) — เก็บของ call ล่าสุดเท่านั้น</summary>
    public IReadOnlyList<AnthropicHistoryMessage>? LastHistoryReceived { get; private set; }

    public Task<ApiKeyValidationOutcome> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (apiKey.Contains("unknown-outcome", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ApiKeyValidationOutcome.Unknown);
        }

        var outcome = apiKey.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            ? ApiKeyValidationOutcome.Invalid
            : ApiKeyValidationOutcome.Valid;
        return Task.FromResult(outcome);
    }

    public async Task<AnthropicConversationResult> SendWithSingleToolRoundTripAsync(
        string apiKey,
        IReadOnlyList<AnthropicHistoryMessage> history,
        AnthropicToolDefinition tool,
        Func<JsonElement, CancellationToken, Task<string>> executeTool,
        CancellationToken cancellationToken)
    {
        LastHistoryReceived = history;
        var lastText = history.Count > 0 ? history[^1].Text : string.Empty;

        if (!lastText.Contains("roll", StringComparison.OrdinalIgnoreCase))
        {
            return new AnthropicConversationResult($"The AI narrates: {lastText}", ToolCall: null);
        }

        var inputJson = lastText.Contains("malformed-tool-input", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(new { })
            : JsonSerializer.Serialize(new { expression = "1d20+5" });
        var input = JsonSerializer.Deserialize<JsonElement>(inputJson);
        var toolResult = await executeTool(input, cancellationToken);

        return new AnthropicConversationResult(
            $"You roll the dice... {toolResult}. The outcome is decided!",
            new AnthropicToolCallRecord(tool.Name, input, toolResult));
    }
}

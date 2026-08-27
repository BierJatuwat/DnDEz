using System.Text.Json;
using DnDEz.Api.Orchestration;

namespace DnDEz.Api.Tests.Fakes;

/// <summary>
/// Sprint D Part 2/3 — ห้ามเรียก Anthropic API จริงในเทสต์อัตโนมัติเด็ดขาด (ไม่มี key จริง, ไม่
/// deterministic, มีต้นทุนจริงเป็นเงิน) — fake ตัวนี้แทนที่ `IAnthropicClient` จริงผ่าน
/// `WebApplicationFactory.ConfigureServices` ในทุกเทสต์ที่แตะ endpoint ที่ inject มัน
///
/// พฤติกรรมกำหนดจากเนื้อหา input เอง ให้เทสต์ควบคุมผลลัพธ์ได้ตรง ๆ โดยไม่ต้อง mock framework ใด ๆ:
///  • `ValidateKeyAsync`: key ที่มีคำว่า "invalid" → Invalid, "unknown-outcome" → Unknown, อื่น ๆ → Valid
///  • `SendTurnAsync`: ข้อความสุดท้ายมีคำว่า "roll" (ไม่สนตัวพิมพ์) **และ** มี tool ให้เสนอ (ไม่ null —
///    จำลอง Manual level ที่ไม่เสนอ tool เลย) → ตอบเป็น tool_use พร้อม input `{expression:"1d20+5"}`
///    (หรือ `{}` ถ้ามีคำว่า "malformed-tool-input" ด้วย จำลอง input ผิดรูปแบบ) ไม่งั้นตอบ narration
///    เฉย ๆ — **ไม่ execute อะไรเองเลย** ตรงตาม contract ใหม่ของ interface
///  • `ContinueWithToolResultAsync`: ตอบ narration ที่อ้างอิงผล tool_result ที่ส่งมา (พิสูจน์ round-trip
///    ทำงานจริง)
/// </summary>
public sealed class FakeAnthropicClient : IAnthropicClient
{
    /// <summary>เทสต์ inspect ได้ว่า history ที่ endpoint ส่งมาจริงมีอะไรบ้าง — เก็บของ call ล่าสุด</summary>
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

    public Task<AnthropicTurnResult> SendTurnAsync(
        string apiKey,
        IReadOnlyList<AnthropicHistoryMessage> history,
        AnthropicToolDefinition? tool,
        CancellationToken cancellationToken)
    {
        LastHistoryReceived = history;
        var lastText = history.Count > 0 ? history[^1].Text : string.Empty;

        var emptyContent = JsonSerializer.Deserialize<JsonElement>("[]");

        if (tool is null || !lastText.Contains("roll", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new AnthropicTurnResult($"The AI narrates: {lastText}", ToolUse: null, emptyContent));
        }

        var inputJson = lastText.Contains("malformed-tool-input", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(new { })
            : JsonSerializer.Serialize(new { expression = "1d20+5" });
        var input = JsonSerializer.Deserialize<JsonElement>(inputJson);
        var toolUseId = $"toolu_{Guid.NewGuid():N}";

        // จำลอง raw content array ของ Anthropic จริง (text block + tool_use block) — เก็บเป็น
        // JsonElement ตรง ๆ เพื่อให้ ContinueWithToolResultAsync echo กลับได้เหมือนของจริง
        var rawContentJson = JsonSerializer.Serialize(new object[]
        {
            new { type = "text", text = "Let me roll for that." },
            new { type = "tool_use", id = toolUseId, name = tool.Name, input },
        });
        var rawContent = JsonSerializer.Deserialize<JsonElement>(rawContentJson);

        return Task.FromResult(new AnthropicTurnResult(
            "Let me roll for that.",
            new AnthropicPendingToolUse(toolUseId, tool.Name, input),
            rawContent));
    }

    public Task<AnthropicTurnResult> ContinueWithToolResultAsync(
        string apiKey,
        IReadOnlyList<AnthropicHistoryMessage> history,
        AnthropicToolDefinition tool,
        JsonElement rawAssistantContent,
        string toolUseId,
        string toolResultText,
        CancellationToken cancellationToken)
    {
        LastHistoryReceived = history;
        var emptyContent = JsonSerializer.Deserialize<JsonElement>("[]");
        return Task.FromResult(new AnthropicTurnResult(
            $"You roll the dice... {toolResultText}. The outcome is decided!",
            ToolUse: null,
            emptyContent));
    }
}

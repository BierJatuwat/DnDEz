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
///  • `SendTurnAsync`: ข้อความสุดท้ายมีคำว่า "roll" (ไม่สนตัวพิมพ์) **และ** มี tool ชื่อ "roll_dice" อยู่ใน
///    `tools` → ตอบเป็น tool_use `roll_dice` พร้อม input `{expression:"1d20+5"}` (หรือ `{}` ถ้ามีคำว่า
///    "malformed-tool-input" ด้วย จำลอง input ผิดรูปแบบ) · **2026-09-22 (Part B)** เพิ่ม trigger
///    `lookup-rule:<key>` / `lookup-monster:<key>` (ต้องมี tool ชื่อนั้นอยู่ใน `tools` ด้วยเช่นกัน) →
///    ตอบ tool_use พร้อม input `{key:"<key>"}` — ไม่งั้นตอบ narration เฉย ๆ **ไม่ execute อะไรเองเลย**
///    ตรงตาม contract ของ interface (client ชั้นนี้แค่รายงานว่า Claude "อยาก" เรียก tool ไหน)
///  • `ContinueWithToolResultAsync`: ตอบ narration ที่อ้างอิงผล tool_result ที่ส่งมา (พิสูจน์ round-trip
///    ทำงานจริง)
/// </summary>
public sealed class FakeAnthropicClient : IAnthropicClient
{
    /// <summary>เทสต์ inspect ได้ว่า history ที่ endpoint ส่งมาจริงมีอะไรบ้าง — เก็บของ call ล่าสุด</summary>
    public IReadOnlyList<AnthropicHistoryMessage>? LastHistoryReceived { get; private set; }

    /// <summary>2026-09-22 — เทสต์ inspect ได้ว่า system prompt ที่ endpoint ประกอบมาจริงมีเนื้อหาอะไร
    /// (regression guard ตรงจุดที่เคยพัง: ก่อนหน้านี้ไม่มี field นี้เลยสักบรรทัด)</summary>
    public string? LastSystemPromptReceived { get; private set; }

    /// <summary>2026-09-22 (Part B) — เทสต์ inspect ได้ว่า endpoint เสนอ tool อะไรบ้างในเทิร์นล่าสุด
    /// (ยืนยันว่า lookup tool ถูกเสนอแม้ตอน autonomy=Manual ต่างจาก roll_dice)</summary>
    public IReadOnlyList<string>? LastToolNamesOffered { get; private set; }

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
        string? system,
        IReadOnlyList<AnthropicHistoryMessage> history,
        IReadOnlyList<AnthropicToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        LastHistoryReceived = history;
        LastSystemPromptReceived = system;
        LastToolNamesOffered = tools.Select(t => t.Name).ToList();
        var lastText = history.Count > 0 ? history[^1].Text : string.Empty;

        var emptyContent = JsonSerializer.Deserialize<JsonElement>("[]");

        string? toolName = null;
        JsonElement input = default;

        var rollTool = tools.SingleOrDefault(t => t.Name == "roll_dice");
        var lookupRuleTool = tools.SingleOrDefault(t => t.Name == "lookup_rule");
        var lookupMonsterTool = tools.SingleOrDefault(t => t.Name == "lookup_monster");
        var journalSearchTool = tools.SingleOrDefault(t => t.Name == "search_campaign_journal");

        if (rollTool is not null && lastText.Contains("roll", StringComparison.OrdinalIgnoreCase))
        {
            toolName = rollTool.Name;
            var inputJson = lastText.Contains("malformed-tool-input", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Serialize(new { })
                : JsonSerializer.Serialize(new { expression = "1d20+5" });
            input = JsonSerializer.Deserialize<JsonElement>(inputJson);
        }
        else if (lookupRuleTool is not null && TryExtractMarkerValue(lastText, "lookup-rule:", out var ruleKey))
        {
            toolName = lookupRuleTool.Name;
            input = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { key = ruleKey }));
        }
        else if (lookupMonsterTool is not null && TryExtractMarkerValue(lastText, "lookup-monster:", out var monsterKey))
        {
            toolName = lookupMonsterTool.Name;
            input = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { key = monsterKey }));
        }
        else if (journalSearchTool is not null && TryExtractMarkerValue(lastText, "search-journal:", out var journalQuery))
        {
            // 2026-09-22 (Part D) — trigger ของ `search_campaign_journal` · marker ตัดที่ช่องว่าง/
            // วรรคตอนตัวแรก คำค้นในเทสต์จึงต้องเป็นคำเดียวติดกัน (เช่น "search-journal:เวลรอธ ...")
            toolName = journalSearchTool.Name;
            input = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { query = journalQuery }));
        }

        if (toolName is null)
        {
            return Task.FromResult(new AnthropicTurnResult($"The AI narrates: {lastText}", ToolUse: null, emptyContent));
        }

        var toolUseId = $"toolu_{Guid.NewGuid():N}";

        // จำลอง raw content array ของ Anthropic จริง (text block + tool_use block) — เก็บเป็น
        // JsonElement ตรง ๆ เพื่อให้ ContinueWithToolResultAsync echo กลับได้เหมือนของจริง
        var rawContentJson = JsonSerializer.Serialize(new object[]
        {
            new { type = "text", text = "Let me check that for you." },
            new { type = "tool_use", id = toolUseId, name = toolName, input },
        });
        var rawContent = JsonSerializer.Deserialize<JsonElement>(rawContentJson);

        return Task.FromResult(new AnthropicTurnResult(
            "Let me check that for you.",
            new AnthropicPendingToolUse(toolUseId, toolName, input),
            rawContent));
    }

    public Task<AnthropicTurnResult> ContinueWithToolResultAsync(
        string apiKey,
        string? system,
        IReadOnlyList<AnthropicHistoryMessage> history,
        IReadOnlyList<AnthropicToolDefinition> tools,
        JsonElement rawAssistantContent,
        string toolUseId,
        string toolResultText,
        CancellationToken cancellationToken)
    {
        LastHistoryReceived = history;
        LastSystemPromptReceived = system;
        LastToolNamesOffered = tools.Select(t => t.Name).ToList();
        var emptyContent = JsonSerializer.Deserialize<JsonElement>("[]");
        return Task.FromResult(new AnthropicTurnResult(
            $"Here's what I found: {toolResultText}",
            ToolUse: null,
            emptyContent));
    }

    // ── ผู้ช่วย DM แบบหลายขั้น (2026-09-23) ─────────────────────────────────────

    /// <summary>คำตอบที่จะคืนทีละรอบตามลำดับ — เทสต์ใส่สคริปต์ไว้ก่อนยิง request · หมดคิวแล้วจะตอบเป็น
    /// ข้อความ "จบงาน" โดยไม่เรียก tool (loop จบเอง) ไม่ใช่ throw — เทสต์ที่อยากเช็คเพดานรอบต้องใส่คิวให้เกินเอง</summary>
    public Queue<ScriptedAgentTurn> AgentScript { get; } = new();

    /// <summary>ทุก call ที่ได้รับตามลำดับ — ใช้ตรวจว่า tool_result ถูกส่งกลับครบ, system prompt มีอะไร,
    /// เสนอ tool ตัวไหนบ้าง</summary>
    public List<AgentCall> AgentCalls { get; } = [];

    public Task<AgentTurnResult> SendAgentTurnAsync(
        string apiKey,
        string? system,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AnthropicToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        AgentCalls.Add(new AgentCall(system, messages.ToList(), tools.Select(t => t.Name).ToList()));
        LastSystemPromptReceived = system;
        LastToolNamesOffered = tools.Select(t => t.Name).ToList();

        var turn = AgentScript.Count > 0 ? AgentScript.Dequeue() : new ScriptedAgentTurn("เสร็จแล้วครับ");
        if (ReferenceEquals(turn, ScriptedAgentTurn.Failure))
        {
            throw new AnthropicApiException("scripted failure");
        }

        if (ReferenceEquals(turn, ScriptedAgentTurn.Crash))
        {
            throw new InvalidOperationException("scripted crash");
        }

        var blocks = new List<object>();
        if (!string.IsNullOrEmpty(turn.Text))
        {
            blocks.Add(new { type = "text", text = turn.Text });
        }

        var toolUses = new List<AnthropicPendingToolUse>();
        foreach (var (name, input) in turn.Tools)
        {
            var id = $"toolu_{Guid.NewGuid():N}";
            var inputElement = JsonSerializer.SerializeToElement(input);
            blocks.Add(new { type = "tool_use", id, name, input = inputElement });
            toolUses.Add(new AnthropicPendingToolUse(id, name, inputElement));
        }

        return Task.FromResult(new AgentTurnResult(
            turn.Text, toolUses, JsonSerializer.SerializeToElement(blocks)));
    }

    /// <summary>ดึงค่าที่อยู่หลัง marker (เช่น "lookup-rule:blinded" → "blinded") — ตัดที่ช่องว่าง/
    /// เครื่องหมายวรรคตอนตัวแรกที่เจอ ไม่มี marker เลย = คืน false</summary>
    private static bool TryExtractMarkerValue(string text, string marker, out string value)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            value = string.Empty;
            return false;
        }

        var afterMarker = text[(index + marker.Length)..];
        var endIndex = afterMarker.IndexOfAny([' ', '.', ',', '?', '!']);
        value = endIndex < 0 ? afterMarker.Trim() : afterMarker[..endIndex].Trim();
        return value.Length > 0;
    }
}

/// <summary>หนึ่งรอบที่ fake จะตอบ — <see cref="Tools"/> ว่าง = ตอบข้อความจบงาน · input เป็น object ธรรมดา
/// (anonymous type ได้) fake แปลงเป็น JsonElement ให้เอง</summary>
public sealed record ScriptedAgentTurn(string Text, params (string Name, object Input)[] Tools)
{
    /// <summary>ใส่ในคิวเพื่อจำลอง Anthropic ล่มในรอบนั้น (fake โยน <c>AnthropicApiException</c>)</summary>
    public static readonly ScriptedAgentTurn Failure = new("__failure__");

    /// <summary>จำลองความผิดพลาดที่ runner ไม่ได้จับ (exception ลอยถึง middleware → rollback transaction ปัจจุบัน)
    /// ใช้พิสูจน์ว่างานที่ checkpoint ไปแล้วไม่ถูก rollback ตาม</summary>
    public static readonly ScriptedAgentTurn Crash = new("__crash__");
}

public sealed record AgentCall(string? System, IReadOnlyList<AgentMessage> Messages, IReadOnlyList<string> ToolNames);

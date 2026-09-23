using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DnDEz.Api.Orchestration;

/// <summary>
/// Sprint D Part 2/3 — implementation จริงของ <see cref="IAnthropicClient"/> เรียก Messages API ตรง ๆ
/// ผ่าน HttpClient (ไม่ใช้ SDK ของ Anthropic — โปรเจกต์นี้เก็บ dependency ให้น้อยที่สุดเท่าที่จำเป็น
/// และ wire format ของ Messages API เสถียร/นิ่งพอที่จะเรียกตรงได้โดยไม่ต้องพึ่ง SDK)
///
/// ⚠️ จุดที่ห้ามพลาด:
///  • ห้าม log ค่า apiKey หรือ raw request/response body ที่อาจมีเนื้อหา user-generated เต็ม ๆ
///    (เนื้อหาแคมเปญอาจมี secret ของ DM เอง) — โยน exception ธรรมดา ไม่ log เนื้อหาเข้าไปในข้อความ error
///  • content array ของ assistant message ต้องส่งกลับไปแบบ **เป๊ะเดิมทุก byte** ตอน
///    `ContinueWithToolResultAsync` (Anthropic ต้องการ echo กลับตรง ๆ ไม่ใช่แค่สร้าง tool_use block
///    ใหม่เอง) ถึงต้องคืนเป็น `JsonElement` ดิบจาก response ตรง ๆ ไม่ใช่ deserialize แล้ว reserialize
///    ผ่าน DTO ของเรา — และต้อง**คงได้แม้ข้าม HTTP request** (persist ลง `Proposal.ToolCallJson` ระหว่าง
///    รอ DM approve) จึงเลือก `JsonElement` (serializable ตรงไปตรงมา) แทน `JsonNode`/`JsonObject`
///    (mutable, ไม่เหมาะเก็บเป็น persisted state)
/// </summary>
public sealed class AnthropicClient(HttpClient httpClient) : IAnthropicClient
{
    private const string ApiVersion = "2023-06-01";
    private const string Model = "claude-sonnet-5";
    private const int MaxTokens = 1024;
    private const string MessagesPath = "v1/messages";

    public async Task<ApiKeyValidationOutcome> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        // request เบาที่สุดเท่าที่ทำได้ — max_tokens=1 ยังต้อง valid request ตาม schema ของ Anthropic
        // แต่แทบไม่มีต้นทุน token generation จริง จุดประสงค์คือเช็ค auth ไม่ใช่ได้คำตอบ
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 1,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        };

        using var request = BuildRequest(apiKey, body);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return ApiKeyValidationOutcome.Valid;
            }

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? ApiKeyValidationOutcome.Invalid
                : ApiKeyValidationOutcome.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiKeyValidationOutcome.Unknown;
        }
    }

    public async Task<AnthropicTurnResult> SendTurnAsync(
        string apiKey,
        string? system,
        IReadOnlyList<AnthropicHistoryMessage> history,
        IReadOnlyList<AnthropicToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var messages = BuildMessagesArray(history);
        var toolsArray = tools.Count == 0 ? null : BuildToolsArray(tools);

        var response = await SendMessagesRequestAsync(apiKey, system, messages, toolsArray, cancellationToken);
        return ExtractTurnResult(response);
    }

    public async Task<AnthropicTurnResult> ContinueWithToolResultAsync(
        string apiKey,
        string? system,
        IReadOnlyList<AnthropicHistoryMessage> history,
        IReadOnlyList<AnthropicToolDefinition> tools,
        JsonElement rawAssistantContent,
        string toolUseId,
        string toolResultText,
        CancellationToken cancellationToken)
    {
        var messages = BuildMessagesArray(history);

        // echo content array ของรอบแรกกลับไปเป๊ะ ๆ (ดู doc comment ของคลาส) — แปลง JsonElement เป็น
        // JsonNode ผ่าน string round-trip เพื่อกัน "JsonElement ผูกกับ JsonDocument เดิมที่อาจถูก
        // dispose ไปแล้ว" (เช่นกรณีที่ rawAssistantContent เดินทางผ่าน DB round-trip มา)
        var assistantContentNode = JsonNode.Parse(rawAssistantContent.GetRawText());
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContentNode });
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = toolResultText,
            }),
        });

        var toolsArray = tools.Count == 0 ? null : BuildToolsArray(tools);
        var response = await SendMessagesRequestAsync(apiKey, system, messages, toolsArray, cancellationToken);
        return ExtractTurnResult(response);
    }

    private static JsonArray BuildMessagesArray(IReadOnlyList<AnthropicHistoryMessage> history)
    {
        var messages = new JsonArray();
        foreach (var message in history)
        {
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Text });
        }

        return messages;
    }

    private static JsonArray BuildToolsArray(IReadOnlyList<AnthropicToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonSerializer.SerializeToNode(tool.InputSchema),
            });
        }

        return array;
    }

    private async Task<JsonObject> SendMessagesRequestAsync(
        string apiKey, string? system, JsonArray messages, JsonArray? tools, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = MaxTokens,
            ["messages"] = messages,
        };
        if (tools is not null)
        {
            body["tools"] = tools;
        }
        // 2026-09-22: null/ว่าง = ไม่ส่ง field นี้เลย (ไม่ใช่ "" ว่าง) — Anthropic รับ system ที่ไม่มี
        // field ได้ปกติ แต่ไม่มีเหตุผลจะส่ง "" เปล่า ๆ เปลืองไปเฉย ๆ
        if (!string.IsNullOrWhiteSpace(system))
        {
            body["system"] = system;
        }

        using var request = BuildRequest(apiKey, body);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // ไม่ log response body — อาจมีรายละเอียด error ที่ embed เนื้อหา request กลับมา
            throw new AnthropicApiException(
                $"Anthropic API คืนสถานะ {(int)response.StatusCode} ({response.StatusCode})");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(responseBody)?.AsObject()
            ?? throw new AnthropicApiException("Anthropic API คืน response ที่ parse เป็น JSON object ไม่ได้");
    }

    private static AnthropicTurnResult ExtractTurnResult(JsonObject response)
    {
        var contentNode = response["content"]
            ?? throw new AnthropicApiException("Anthropic response ไม่มี content array");
        var contentArray = contentNode.AsArray();

        var textParts = new List<string>();
        JsonObject? toolUseBlock = null;

        foreach (var blockNode in contentArray)
        {
            if (blockNode is not JsonObject block)
            {
                continue;
            }

            var type = block["type"]?.GetValue<string>();
            if (type == "text")
            {
                textParts.Add(block["text"]?.GetValue<string>() ?? string.Empty);
            }
            else if (type == "tool_use" && toolUseBlock is null)
            {
                toolUseBlock = block; // จำกัด 1 tool call ต่อเทิร์นตามขอบเขต skeleton
            }
        }

        // JsonNode -> JsonElement ผ่าน string round-trip (contentNode จะถูก reuse/dispose ไม่ได้
        // ถ้า caller เก็บ reference ไว้ข้าม request — JsonElement ที่ parse จาก string ใหม่ปลอดภัยกว่า)
        var rawAssistantContent = JsonSerializer.Deserialize<JsonElement>(contentNode.ToJsonString());

        AnthropicPendingToolUse? toolUse = null;
        if (toolUseBlock is not null)
        {
            var id = toolUseBlock["id"]!.GetValue<string>();
            var name = toolUseBlock["name"]!.GetValue<string>();
            var input = JsonSerializer.Deserialize<JsonElement>(toolUseBlock["input"]!.ToJsonString());
            toolUse = new AnthropicPendingToolUse(id, name, input);
        }

        return new AnthropicTurnResult(string.Join("\n", textParts), toolUse, rawAssistantContent);
    }

    private static HttpRequestMessage BuildRequest(string apiKey, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MessagesPath)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        return request;
    }
}

/// <summary>โยนเมื่อ Anthropic API คืนผลที่ใช้ต่อไม่ได้ (error status, response shape ผิดคาด) —
/// ไม่ใช่ปัญหาของ key เอง (นั่นคือ ApiKeyValidationOutcome.Invalid) จึงแยก exception type ต่างหาก</summary>
public sealed class AnthropicApiException(string message) : Exception(message);

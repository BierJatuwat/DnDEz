using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DnDEz.Api.Orchestration;

/// <summary>
/// Sprint D Part 2 — implementation จริงของ <see cref="IAnthropicClient"/> เรียก Messages API ตรง ๆ
/// ผ่าน HttpClient (ไม่ใช้ SDK ของ Anthropic — โปรเจกต์นี้เก็บ dependency ให้น้อยที่สุดเท่าที่จำเป็น
/// และ wire format ของ Messages API เสถียร/นิ่งพอที่จะเรียกตรงได้โดยไม่ต้องพึ่ง SDK)
///
/// ⚠️ จุดที่ห้ามพลาด:
///  • ห้าม log ค่า apiKey หรือ raw request/response body ที่อาจมีเนื้อหา user-generated เต็ม ๆ
///    (เนื้อหาแคมเปญอาจมี secret ของ DM เอง) — โยน exception ธรรมดา ไม่ log เนื้อหาเข้าไปในข้อความ error
///  • content array ของ assistant message รอบแรก (ที่มี tool_use block) ต้องส่งกลับไปแบบ **เป๊ะเดิม
///    ทุก byte** ในรอบสอง (Anthropic ต้องการ echo กลับตรง ๆ ไม่ใช่แค่สร้าง tool_use block ใหม่เอง)
///    ถึงต้องเก็บเป็น JsonNode ดิบจาก response ตรง ๆ ไม่ใช่ deserialize แล้ว reserialize ผ่าน DTO ของเรา
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

    public async Task<AnthropicConversationResult> SendWithSingleToolRoundTripAsync(
        string apiKey,
        IReadOnlyList<AnthropicHistoryMessage> history,
        AnthropicToolDefinition tool,
        Func<JsonElement, CancellationToken, Task<string>> executeTool,
        CancellationToken cancellationToken)
    {
        var messages = new JsonArray();
        foreach (var message in history)
        {
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Text });
        }

        var tools = new JsonArray(new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["input_schema"] = JsonSerializer.SerializeToNode(tool.InputSchema),
        });

        var firstResponse = await SendMessagesRequestAsync(apiKey, messages, tools, cancellationToken);
        var (text, toolUse) = ExtractTextAndToolUse(firstResponse);

        if (toolUse is null)
        {
            return new AnthropicConversationResult(text, ToolCall: null);
        }

        var toolUseId = toolUse["id"]!.GetValue<string>();
        var toolName = toolUse["name"]!.GetValue<string>();
        var toolInputNode = toolUse["input"]!;
        var toolInputElement = JsonSerializer.Deserialize<JsonElement>(toolInputNode.ToJsonString());

        var toolResultText = await executeTool(toolInputElement, cancellationToken);

        // assistant message ต้อง echo content array ของรอบแรกกลับไปเป๊ะ ๆ (ดู doc comment ของคลาส)
        var assistantContent = firstResponse["content"]!.DeepClone();
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });
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

        var secondResponse = await SendMessagesRequestAsync(apiKey, messages, tools, cancellationToken);
        var (finalText, _) = ExtractTextAndToolUse(secondResponse); // จำกัด 1 รอบตามขอบเขต skeleton

        return new AnthropicConversationResult(
            finalText,
            new AnthropicToolCallRecord(toolName, toolInputElement, toolResultText));
    }

    private async Task<JsonObject> SendMessagesRequestAsync(
        string apiKey, JsonArray messages, JsonArray tools, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = MaxTokens,
            ["messages"] = messages,
            ["tools"] = tools,
        };

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

    private static (string Text, JsonObject? ToolUse) ExtractTextAndToolUse(JsonObject response)
    {
        var contentArray = response["content"]?.AsArray()
            ?? throw new AnthropicApiException("Anthropic response ไม่มี content array");

        var textParts = new List<string>();
        JsonObject? toolUse = null;

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
            else if (type == "tool_use")
            {
                toolUse = block;
            }
        }

        return (string.Join("\n", textParts), toolUse);
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

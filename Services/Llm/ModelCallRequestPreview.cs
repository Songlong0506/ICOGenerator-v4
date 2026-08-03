using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Dựng chuỗi JSON "request đã gửi" hiển thị trong màn Call Log. Đây KHÔNG phải body thật đi trên dây
/// (SDK OpenAI mới là nơi dựng nó) mà là bản mô tả tương đương — nên nó phải soi gương
/// <see cref="LlmRequestCompatibilityHandler"/>: trường <c>thinking</c> chỉ chèn cho endpoint
/// OpenAI-<i>compatible</i> (không phải OpenAI thật), và <c>temperature</c> bị bỏ với model reasoning của
/// OpenAI (chúng từ chối giá trị khác mặc định). Tách khỏi <see cref="ModelCallLoggingChatClient"/> vì đây
/// là việc "định dạng để hiển thị", không phải việc điều phối lời gọi — và tách ra thì test được riêng.
/// </summary>
internal static class ModelCallRequestPreview
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    public static string Build(AiModel model, IList<ChatMessage> messages, ChatOptions options, int maxTokens, bool streaming)
    {
        var isOpenAi = OpenAiCompatibility.IsOpenAiHost(OpenAiCompatibility.HostOf(model.Endpoint));
        var dropTemperature = isOpenAi && OpenAiCompatibility.IsReasoningModel(model.ModelId);

        var node = JsonSerializer.SerializeToNode(new
        {
            model = model.ModelId,
            messages = messages.Select(m => new { role = m.Role.Value, content = m.Text }),
            temperature = options.Temperature,
            max_tokens = maxTokens,
            // Không phải lúc nào cũng true: mức json_schema của structured output là một round-trip đơn.
            stream = streaming,
            // Tool tóm tắt bằng TÊN (JSON schema đầy đủ do SDK OpenAI dựng ở downstream).
            tools = options.Tools?.Select(t => t.Name) ?? Enumerable.Empty<string>(),
        })!.AsObject();

        if (dropTemperature)
            node.Remove("temperature");

        // response_format chính là thứ endpoint 400 khi không hỗ trợ mức được xin (DeepSeek: "This
        // response_format type is unavailable now"), nên call log — chỗ đầu tiên người ta mở ra khi gặp lỗi
        // đó — bắt buộc phải cho thấy mức nào đã thực sự đi ra.
        if (ResponseFormat(options.ResponseFormat) is { } responseFormat)
            node["response_format"] = responseFormat;

        if (!isOpenAi)
            node["thinking"] = new JsonObject { ["type"] = "disabled" };

        return node.ToJsonString(SerializeOptions);
    }

    private static JsonObject? ResponseFormat(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson { Schema: not null } => new JsonObject { ["type"] = "json_schema" },
        ChatResponseFormatJson => new JsonObject { ["type"] = "json_object" },
        _ => null
    };
}

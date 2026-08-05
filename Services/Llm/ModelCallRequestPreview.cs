using System.Text;
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

        // Số thứ tự ảnh chạy suốt CẢ REQUEST (không reset theo từng message) để khớp một-một với thứ tự
        // ModelCallImageCollector lưu file — lệch đánh số là bấm xem ảnh này lại ra ảnh khác.
        var imageIndex = 0;
        var previewMessages = messages
            .Select(m => new { role = m.Role.Value, content = Content(m, ref imageIndex) })
            .ToList();

        var node = JsonSerializer.SerializeToNode(new
        {
            model = model.ModelId,
            messages = previewMessages,
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

        // Cỡ GÓI TIN thật sự đi trên dây, ước lượng. Không phải thông tin trang trí: một lời gọi mang ảnh
        // vượt trần body của gateway/proxy trước endpoint sẽ chết mà KHÔNG có mã HTTP nào để đọc lý do
        // (xem EndpointQuirks.RequestNeverReachedModel), và khi đó câu hỏi đầu tiên luôn là "request bao
        // nhiêu MB". Không có con số này thì phải ngồi cộng tay từng part ảnh trong log.
        node["_approxBodyBytes"] = ApproxBodyBytes(messages);

        return node.ToJsonString(SerializeOptions);
    }

    /// <summary>
    /// Phần <c>content</c> của một message. Message toàn chữ giữ nguyên dạng CHUỖI như trước (UI "Dễ đọc"
    /// và mọi log cũ đều dựa vào dạng đó). Message có ảnh mới chuyển sang dạng MẢNG các part.
    ///
    /// Phần ảnh cố tình chỉ ghi mô tả (tên, media type, số byte, số thứ tự) chứ KHÔNG nhúng base64: 12
    /// screenshot của một tài liệu là ~1MB thô, tức ~1.3MB base64 mỗi dòng log trong một bảng mà BudgetGuard
    /// quét trước mỗi lời gọi model. Bytes đi ra đĩa (xem <see cref="ModelCallImageStore"/>), ở đây chỉ giữ
    /// <c>index</c> để UI xin lại đúng tấm ảnh.
    /// </summary>
    private static object Content(ChatMessage message, ref int imageIndex)
    {
        var parts = new List<object>();
        var hasNonText = false;

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    parts.Add(new { type = "text", text = text.Text });
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    hasNonText = true;
                    parts.Add(new
                    {
                        type = "image",
                        index = ++imageIndex,
                        name = string.IsNullOrWhiteSpace(data.Name) ? $"image-{imageIndex}" : data.Name,
                        mediaType = data.MediaType ?? "image/png",
                        bytes = data.Data.Length,
                    });
                    break;
                case DataContent data:
                    hasNonText = true;
                    parts.Add(new { type = "data", mediaType = data.MediaType ?? "application/octet-stream", bytes = data.Data.Length });
                    break;
                case UriContent uri:
                    hasNonText = true;
                    parts.Add(new { type = "uri", uri = uri.Uri.ToString(), mediaType = uri.MediaType });
                    break;
            }
        }

        return hasNonText ? parts : message.Text;
    }

    /// <summary>
    /// Cỡ gói tin ước lượng: chữ tính theo byte UTF-8, ảnh tính theo cỡ SAU khi base64 hóa (4 byte cho mỗi
    /// 3 byte thô) — ảnh không đi trên dây dưới dạng nhị phân mà nằm trong chuỗi JSON, nên 1MB ảnh là
    /// ~1.33MB gói tin. Dùng chung với <see cref="ModelCallLoggingChatClient"/>: một lời gọi chết ở tầng
    /// truyền tải cần nêu được con số này ngay trong thông báo lỗi.
    /// </summary>
    public static long ApproxBodyBytes(IList<ChatMessage> messages)
    {
        long total = 0;
        foreach (var content in messages.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case TextContent text:
                    total += Encoding.UTF8.GetByteCount(text.Text ?? string.Empty);
                    break;
                case DataContent data:
                    total += (data.Data.Length + 2) / 3 * 4;
                    break;
            }
        }
        return total;
    }

    private static JsonObject? ResponseFormat(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson { Schema: not null } => new JsonObject { ["type"] = "json_schema" },
        ChatResponseFormatJson => new JsonObject { ["type"] = "json_object" },
        _ => null
    };
}

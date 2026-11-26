using ICOGenerator.Localization;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

/// <summary>
/// Dựng cấu hình model để GỌI THỬ từ form Add/Edit của trang Models — dùng chung cho nút Test Connection
/// (<see cref="TestAiModelConnectionUseCase"/>) và Test Prompt Caching (<see cref="TestAiModelPromptCacheUseCase"/>),
/// để hai nút không lệch nhau về luật "ApiKey để trống = dùng key đã lưu" (key thật không bao giờ về browser).
/// </summary>
internal static class AiModelTestProbe
{
    /// <summary>Probe đã sẵn để gọi, hoặc câu lỗi hiện thẳng lên modal (Probe = null).</summary>
    public static async Task<(AiModel? Probe, string? Error)> ResolveAsync(
        AppDbContext db, AiModel input, CancellationToken cancellationToken)
    {
        var modelId = input.ModelId?.Trim() ?? string.Empty;
        if (modelId.Length == 0)
            return (null, Loc.T("TestAiModelConnectionUseCase.ExecuteAsync"));

        var endpoint = input.Endpoint?.Trim() ?? string.Empty;
        // claude-code://… = chạy qua Claude Code CLI đã đăng nhập trên máy: không có URL HTTP, không dùng ApiKey.
        var viaClaudeCode = ClaudeCodeCli.IsClaudeCodeEndpoint(endpoint);
        if (!viaClaudeCode
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            return (null, Loc.T("TestAiModelConnectionUseCase.ExecuteAsync.2"));

        var apiKey = (input.ApiKey ?? string.Empty).Trim();
        if (apiKey.Length == 0 && input.Id != Guid.Empty)
            apiKey = await db.AiModels
                .Where(x => x.Id == input.Id)
                .Select(x => x.ApiKey)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        if (apiKey.Length == 0 && !viaClaudeCode)
            return (null, Loc.T("TestAiModelConnectionUseCase.ExecuteAsync.3"));

        // Ba trường quyết định "có gọi được không", cộng hai cờ đổi chính request đi ra (cache_control, header
        // sticky session) — lấy theo FORM, chưa lưu cũng được, vì người ta bấm thử chính là để quyết có lưu không.
        // Cờ vision/structured-output không ảnh hưởng lời gọi thử.
        return (new AiModel
        {
            ModelId = modelId,
            Endpoint = endpoint,
            ApiKey = apiKey,
            DisablePromptCache = input.DisablePromptCache,
            StickySessionRouting = input.StickySessionRouting,
        }, null);
    }
}

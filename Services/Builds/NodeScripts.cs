using System.Text.Json;

namespace ICOGenerator.Services.Builds;

/// <summary>
/// Đọc phần <c>scripts</c> của một <c>package.json</c>. Tách riêng vì cổng build chỉ quan tâm đúng một
/// câu hỏi — "dự án này có lệnh build thật không" — và câu trả lời phải chịu được file hỏng: agent có
/// thể sinh ra một package.json sai cú pháp, và khi đó cổng build phải BỎ QUA phần Node chứ không được
/// làm gãy cả lượt chấm bằng một JsonException ném từ giữa vòng lặp của worker.
/// </summary>
public static class NodeScripts
{
    public static bool HasBuildScript(string? packageJsonContent)
    {
        if (string.IsNullOrWhiteSpace(packageJsonContent))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(packageJsonContent);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
                return false;
            if (!scripts.TryGetProperty("build", out var build) || build.ValueKind != JsonValueKind.String)
                return false;

            return !string.IsNullOrWhiteSpace(build.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

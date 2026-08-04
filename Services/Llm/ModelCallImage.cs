using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>Một ảnh đi kèm lượt gọi model: vị trí trong request, tên hiển thị, media type, và bytes.</summary>
/// <param name="Index">Thứ tự ảnh trong lượt gọi (1-based) — khóa để UI xin lại đúng tấm đó.</param>
/// <param name="Name">Tên hiển thị do nơi tạo ảnh đặt (vd "tai-lieu.docx › figure-3.png"). Rỗng nếu không đặt.</param>
public sealed record ModelCallImage(int Index, string Name, string MediaType, byte[] Bytes)
{
    /// <summary>Đuôi file để lưu ra đĩa, suy từ media type. Không nhận ra ⇒ .bin (vẫn tải về xem được).</summary>
    public string Extension => MediaType.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpeg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => "bin",
    };
}

/// <summary>
/// Gom các ảnh có trong một request để log lại được. Tách khỏi <see cref="ModelCallRequestPreview"/> (vốn chỉ
/// dựng phần CHỮ để hiển thị) vì đây là việc cầm bytes, có trần dung lượng riêng và có thể tắt bằng config.
///
/// Vì sao cần: <c>AgentModelCallLog</c> chỉ lưu <c>RequestJson</c> — một bản dựng lại từ phần text của
/// message — nên mọi ảnh đã gửi biến mất khỏi log. Hệ quả khi truy lỗi: ba tình huống hoàn toàn khác nhau
/// (model không có vision / ảnh bị cắt vì chạm trần / đọc file ảnh lỗi nên bỏ qua) để lại log giống hệt nhau,
/// không cách nào phân biệt.
/// </summary>
public static class ModelCallImageCollector
{
    public static IReadOnlyList<ModelCallImage> Collect(IEnumerable<ChatMessage> messages, long maxTotalBytes)
    {
        List<ModelCallImage>? images = null;
        long total = 0;

        foreach (var content in messages.SelectMany(m => m.Contents))
        {
            if (content is not DataContent data || !data.HasTopLevelMediaType("image"))
                continue;

            var bytes = data.Data.ToArray();
            // Chạm trần thì DỪNG hẳn thay vì bỏ qua tấm này rồi lấy tấm sau: người đọc log cần "N tấm đầu
            // được lưu", chứ không phải một tập lỗ chỗ mà số thứ tự không còn khớp với thứ tự gửi.
            if (total + bytes.Length > maxTotalBytes)
                break;

            images ??= new List<ModelCallImage>();
            total += bytes.Length;
            images.Add(new ModelCallImage(images.Count + 1, data.Name ?? string.Empty, data.MediaType ?? "image/png", bytes));
        }

        return (IReadOnlyList<ModelCallImage>?)images ?? Array.Empty<ModelCallImage>();
    }
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ICOGenerator.Data;

// Đọc bộ dữ liệu seed từ file NDJSON nhúng trong assembly (Data/SeedData/*.ndjson — xem
// <EmbeddedResource> trong ICOGenerator.csproj).
//
// Vì sao không để mảng khởi tạo trong C# như trước: Associates + OrgUnits là DỮ LIỆU (1549 + 195 bản
// ghi, ~38k dòng) nhưng vẫn phải biên dịch thành IL, làm phình assembly và bắt Roslyn/IDE parse lại
// mỗi lần gõ trong thư mục Data — rebuild dự án giảm từ ~16s xuống ~5s sau khi đưa ra ngoài.
//
// Vì sao NDJSON (mỗi bản ghi MỘT dòng) chứ không phải một mảng JSON: mảng nằm trọn trên một dòng
// 800KB thì `git diff` vô dụng; tách dòng thì lần đồng bộ lại từ HR_Portal sau này review được từng
// bản ghi thay đổi, mà dung lượng vẫn y hệt.
internal static class SeedDataResource
{
    // PHẢI khớp với tuỳ chọn dùng lúc sinh file, nếu không dữ liệu đọc lên sẽ lệch: WhenWritingDefault
    // để trường mang giá trị mặc định bị lược khỏi file (đọc lên thì property tự nhận đúng giá trị mặc
    // định đó), UnsafeRelaxedJsonEscaping để ký tự non-ASCII giữ nguyên dạng.
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T[] Load<T>(string resourceName)
    {
        using var stream = typeof(SeedDataResource).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Không tìm thấy resource seed '{resourceName}' trong assembly. Kiểm tra mục " +
                "<EmbeddedResource> (thuộc tính LogicalName) trong ICOGenerator.csproj.");

        using var reader = new StreamReader(stream);
        var items = new List<T>();
        while (reader.ReadLine() is { } line)
        {
            // Bỏ qua dòng trống (điển hình là dòng cuối file) để file kết thúc bằng newline vẫn đọc được.
            if (string.IsNullOrWhiteSpace(line))
                continue;

            items.Add(JsonSerializer.Deserialize<T>(line, Options)
                ?? throw new InvalidOperationException(
                    $"Resource seed '{resourceName}' có dòng {items.Count + 1} là JSON null."));
        }

        if (items.Count == 0)
            throw new InvalidOperationException($"Resource seed '{resourceName}' rỗng.");

        return items.ToArray();
    }
}

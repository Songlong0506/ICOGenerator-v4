using System.Text.Json;
using System.Text.Json.Serialization;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đọc trường <c>known</c> của một dòng bản đồ khi nó tới ở dạng CHUỖI thay vì mảng, và luôn ghi ra mảng.
///
/// <para>
/// <b>Vì sao cần.</b> Trường này từng là một ô tóm tắt duy nhất, nên mọi dự án đang dở dang có
/// <c>Project.RequirementCoverageMap</c> chứa <c>"known":"…"</c>. Không có converter thì lần đọc đầu tiên
/// sau khi đổi kiểu ném <see cref="JsonException"/> ⇒ bản đồ về rỗng ⇒ khối "Bản đồ hiện có" của lượt
/// chắt lọc kế tiếp trống trơn, và model dựng lại bản đồ CHỈ từ vài lượt mới: cả buổi phỏng vấn đã khai
/// thác được coi như chưa từng xảy ra. Đây là hình dạng tệ nhất của một lần đổi schema — không ai thấy
/// lỗi, chỉ thấy tiến độ khai thác tự nhảy về đầu.
/// </para>
///
/// <para>
/// Cũng đỡ luôn chiều LLM: model yếu bỏ qua schema và trả về một chuỗi thì lượt chắt lọc vẫn dùng được
/// thay vì hỏng cả bản đồ.
/// </para>
///
/// <para>
/// Đăng ký ở <see cref="CoverageMapParser"/> (và ở đường parse tay của <see cref="RequirementCoverageService"/>)
/// chứ KHÔNG gắn <c>[JsonConverter]</c> lên thuộc tính: cùng lớp contract ấy còn được đem đi sinh JSON
/// schema cho structured output, mà bộ sinh schema của System.Text.Json trả về schema RỖNG (kiểu bất kỳ)
/// cho một thuộc tính có converter tự viết — tức là mất đúng ràng buộc "mảng chuỗi" mà structured output
/// sinh ra để có.
/// </para>
/// </summary>
public sealed class CoverageKnownJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Bản đồ cũ: một ô tóm tắt ⇒ một phần tử. Rỗng thì là danh sách rỗng, không phải một phần tử rỗng.
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = (reader.GetString() ?? string.Empty).Trim();
            return text.Length == 0 ? new List<string>() : new List<string> { text };
        }

        if (reader.TokenType == JsonTokenType.Null)
            return new List<string>();

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Trường 'known' phải là mảng chuỗi hoặc chuỗi, nhận được {reader.TokenType}.");

        var items = new List<string>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return items;
                case JsonTokenType.String:
                    var value = (reader.GetString() ?? string.Empty).Trim();
                    if (value.Length > 0)
                        items.Add(value);
                    break;
                case JsonTokenType.Null:
                    break;
                default:
                    throw new JsonException($"Phần tử của 'known' phải là chuỗi, nhận được {reader.TokenType}.");
            }
        }

        throw new JsonException("Mảng 'known' không được đóng.");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}

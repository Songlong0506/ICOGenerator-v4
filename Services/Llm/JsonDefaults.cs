using System.Text.Json;

namespace ICOGenerator.Services.Llm;

public static class JsonDefaults
{
    /// <summary>
    /// Dùng chung cho các parser đọc JSON model trả về (BA chat reply, readiness, requirement draft):
    /// model không đảm bảo đúng hoa/thường tên field nên luôn so khớp không phân biệt hoa thường.
    /// </summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
}

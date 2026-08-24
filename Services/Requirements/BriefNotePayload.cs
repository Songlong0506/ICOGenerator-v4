using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Định dạng của <see cref="ICOGenerator.Domain.AgentTask.Input"/> cho run "Write Requirement" được khởi
/// động từ các ghi chú ghim trên bản xem trước Product Brief: JSON danh sách <see cref="BriefNote"/>.
/// Input rỗng ⇒ run soạn tài liệu bình thường (rà cả hội thoại); có ghi chú ⇒ worker rẽ sang vòng SỬA CÓ
/// PHẠM VI. Một chỗ duy nhất biết định dạng này, dùng chung cho bên ghi (ReviseBriefFromNotesUseCase) và
/// bên đọc (AgentTaskWorker) — không nơi nào khác được tự parse.
/// </summary>
public static class BriefNotePayload
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string Serialize(IReadOnlyList<BriefNote> notes) => JsonSerializer.Serialize(notes);

    /// <returns>Danh sách ghi chú KHÔNG rỗng, hoặc null khi input không phải payload ghi chú (run thường,
    /// JSON hỏng, hoặc mọi ghi chú đều rỗng) — mọi nhánh null đều rơi về đường soạn tài liệu bình thường.</returns>
    public static IReadOnlyList<BriefNote>? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        try
        {
            var notes = JsonSerializer.Deserialize<List<BriefNote>>(input, Options);
            var clean = notes?.Where(n => !string.IsNullOrWhiteSpace(n.Note)).ToList();
            return clean is { Count: > 0 } ? clean : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

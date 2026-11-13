using System.Text.Encodings.Web;
using System.Text.Json;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// ẢNH CHỤP "trước lượt gộp gần nhất" của ba cột mà lượt distill bản đồ bao phủ ghi đè
/// (<see cref="Project.RequirementCoverageMap"/>, <see cref="Project.OpenQuestions"/>,
/// <see cref="Project.WorkedExamples"/>) cùng con trỏ <see cref="Project.CoverageHarvestedTurnCount"/> —
/// lưu ở <see cref="Project.CoverageUndoSnapshot"/>.
///
/// <para>
/// <b>Vì sao cần.</b> Bản đồ gộp LŨY TIẾN: lượt distill đọc bản đang lưu + các lượt mới rồi ghi đè. Nên
/// khi người dùng "✎ sửa" lượt vừa gửi, kéo lùi con trỏ thôi là chưa đủ — câu GỐC đã được gộp vào bản đồ
/// ngay ở đầu lượt gốc, và lượt chạy lại chắt câu đã sửa CHỒNG LÊN một bản đồ vẫn còn chứa ý cũ. Câu mới
/// phủ định câu cũ thì model còn có cơ hội xoá; câu mới nói sang chuyện khác thì ý sai ở lại mãi mãi,
/// đúng thứ mà nút sửa sinh ra để dẹp. Có ảnh chụp, đường sửa trả ba cột về ĐÚNG trạng thái trước khi
/// câu gốc được gộp, rồi lượt chạy lại gộp câu đã sửa lên nền sạch.
/// </para>
///
/// <para>
/// <b>Chỉ một ảnh, và chỉ của lượt gộp GẦN NHẤT.</b> Chỉ lượt user cuối sửa được, và lượt gộp chạy ở đầu
/// mỗi lượt chat, nên ảnh của lượt gộp gần nhất chính là trạng thái trước lượt user cuối. Một lượt gộp
/// khác chen vào sau đó (bước soạn Product Brief gộp nốt câu trả lời của BA) thay ảnh bằng một bản ĐÃ
/// chứa lượt user đó — <see cref="TryRestore"/> nhận ra qua con trỏ của ảnh và từ chối, đường sửa lùi về
/// hành vi cũ (chỉ kéo lùi con trỏ). Từ chối là phía an toàn: trả nhầm một ảnh đã chứa câu sai còn tệ
/// hơn không trả gì.
/// </para>
/// </summary>
public static class CoverageUndoSnapshot
{
    // Giữ nguyên chữ có dấu — cùng lựa chọn (và cùng lý do) với CoverageMapParser.
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record Snapshot(int Harvested, string? Map, string? OpenQuestions, string? WorkedExamples);

    /// <summary>
    /// Chụp ba cột + con trỏ ĐANG LƯU. Gọi ngay TRƯỚC khi lượt distill ghi đè chúng, và chỉ trên đường
    /// distill thành công — lượt gộp hỏng không đổi gì nên không có gì để chụp.
    /// </summary>
    public static void Capture(Project project)
    {
        project.CoverageUndoSnapshot = JsonSerializer.Serialize(
            new Snapshot(
                project.CoverageHarvestedTurnCount,
                project.RequirementCoverageMap,
                project.OpenQuestions,
                project.WorkedExamples),
            Json);
    }

    /// <summary>
    /// Trả ba cột + con trỏ về ảnh chụp, NẾU ảnh đó đúng là trạng thái trước lượt user ở vị trí
    /// <paramref name="userTurnIndex"/> (số lượt đứng trước nó). Hai điều kiện, thiếu một là không trả:
    /// <list type="bullet">
    ///   <item>lượt user đó ĐÃ được gộp (con trỏ hiện tại vượt qua nó) — chưa gộp thì bản đồ chưa hề
    ///   thấy câu gốc, không có gì để gỡ;</item>
    ///   <item>ảnh được chụp TRƯỚC lượt user đó (con trỏ của ảnh chưa vượt qua nó) — xem ghi chú của lớp
    ///   về lượt gộp chen vào sau.</item>
    /// </list>
    /// Trả <c>true</c> khi đã trả lại; <c>false</c> ⇒ caller tự kéo lùi con trỏ như trước.
    /// </summary>
    public static bool TryRestore(Project project, int userTurnIndex)
    {
        if (project.CoverageHarvestedTurnCount <= userTurnIndex)
            return false;

        var snapshot = Parse(project.CoverageUndoSnapshot);
        if (snapshot == null || snapshot.Harvested < 0 || snapshot.Harvested > userTurnIndex)
            return false;

        project.RequirementCoverageMap = snapshot.Map;
        project.OpenQuestions = snapshot.OpenQuestions;
        project.WorkedExamples = snapshot.WorkedExamples;
        project.CoverageHarvestedTurnCount = snapshot.Harvested;
        // Ảnh đã dùng xong: trạng thái hiện tại CHÍNH LÀ ảnh đó, và lượt gộp kế tiếp sẽ chụp lại.
        project.CoverageUndoSnapshot = null;
        return true;
    }

    private static Snapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Snapshot>(json, Json);
        }
        catch (JsonException)
        {
            // Ảnh hỏng ⇒ coi như không có: đường sửa lùi về hành vi cũ.
            return null;
        }
    }
}

using ICOGenerator.Contracts.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Frame `done` của POST /Requirements/ChatStream là ĐƯỜNG DUY NHẤT một lượt chat về tới màn hình mà không
// reload trang. Mỗi trường bị bỏ quên ở đó là một thứ người dùng KHÔNG nhìn thấy cho tới lần tải trang kế
// tiếp — và với các BẢNG CHỐT thì đó là hỏng hẳn, không phải chậm: lượt bày bảng vẫn nói "rà bảng bên dưới
// rồi bấm Gửi bảng …" trong khi panel còn ẩn, tức một câu hỏi trỏ vào chỗ trống.
//
// Ca thật: bảng BÁO CÁO bị bỏ quên đúng như vậy (`reportMap`/`reportEntityOptions` không có trong frame,
// trong khi requirements.js vẫn gọi renderReportMap(data.reportMap, data.reportEntityOptions)). Người dùng
// đọc lời mời rà bảng, không thấy bảng đâu, rồi bấm nút "Tạo bản mô tả sản phẩm" đang sáng ngay đó — và
// bảng chỉ hiện ra vì cú bấm ấy tải lại trang.
//
// Không có phép kiểm nào bắt được chuyện đó: bảng vẫn được LƯU đúng vào lượt hội thoại, nên sau F5 mọi thứ
// trông bình thường. Test này là phép kiểm ấy — thêm một trường vào BAChatTurnResult mà quên chở nó qua
// frame là fail ngay tại build.
public class ChatStreamFrameCoverageTests
{
    [Fact]
    public void ChatStreamDoneFrame_CarriesEveryFieldOfTheTurnResult()
    {
        var controller = File.ReadAllText(FindRepoFile(
            Path.Combine("Controllers", "RequirementsController.cs")));

        foreach (var property in typeof(BAChatTurnResult).GetProperties())
        {
            // Phép kiểm là "có ĐỌC tới trường này không", không phải "đọc đúng chỗ nào": tên trường trong
            // frame do client đặt (camelCase, có chỗ đổi tên), nên chốt được thứ duy nhất luôn đúng là lời
            // gọi `result.<Tên>`. Đủ để chặn ca bỏ quên — thứ đã thật sự xảy ra.
            Assert.Contains($"result.{property.Name}", controller, StringComparison.Ordinal);
        }
    }

    // Controllers/ không được copy sang bin của test — đi ngược từ thư mục chạy lên repo root, cùng cách
    // CoveragePromptFixture dò thư mục Prompts.
    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Không tìm thấy " + relativePath + " từ " + AppContext.BaseDirectory);
    }
}

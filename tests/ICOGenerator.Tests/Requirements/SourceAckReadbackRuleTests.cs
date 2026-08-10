using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lượt ĐỌC LẠI TÀI LIỆU NGUỒN phải đọc hết bảng, và phải đặt bảng cạnh lời người dùng.
//
// Ca thật đã gặp trên màn hình (app lập kế hoạch lớp học cho nhân viên Bosch). Người dùng đính kèm một
// file Excel 262 dòng; bản đọc lại của BA sai bốn chỗ kiểm được bằng máy, cả bốn cùng một kiểu — đọc vài
// chục dòng đầu rồi kết luận cho cả bảng:
//
//   BA: "Các nội dung đào tạo thuộc nhiều loại Item Type: COURSE, DOC và WBT"
//        → file có 5 giá trị; WEBINAR (9 dòng) và EUNIVERSITY (2 dòng) rơi mất.
//   BA: "Một số dòng có Assignment Type là REQ hoặc MAN"
//        → còn OPT (5 dòng). Đây là chỗ đắt nhất: ngay câu đầu tiên người dùng đã nói app có "khóa học
//          BẮT BUỘC và khóa học TỰ CHỌN", và REQ/MAN/OPT chính là cột mã hóa việc đó — BA đánh rơi đúng
//          giá trị mang vế "tự chọn", rồi còn ghi vào "Chỗ chưa chắc" là chưa rõ cách phân biệt REQ với MAN.
//   BA: "Required Date và Days Rem hiện đang để trống trong các dòng được cung cấp"
//        → 12 dòng có hạn hoàn thành, một dòng Days Rem = 0 (đã tới hạn). Tệ hơn một chỗ bỏ trống: người
//          dùng đọc lướt thấy hợp lý sẽ bấm "Đúng rồi" và cái sai được đóng dấu xác nhận.
//   BA: minh họa cột Organization bằng HcP/MFW2-LL11-B/C/D và HcP/MFW2-CKD-C
//        → đúng 4 nhóm NHỎ NHẤT (1–6 dòng, tình cờ nằm đầu file); 4 nhóm lớn nhất (38–60 dòng mỗi nhóm)
//          không được nhắc tới dòng nào.
//
// Và một lỗ hổng lớn hơn cả bốn cái trên: BA đọc file như một vật thể độc lập, không hề đặt nó cạnh điều
// người dùng vừa kể. File đến vì BA xin "Master List — nhân viên và các khóa học phải học trong năm",
// nhưng thứ nhận được lại đầy cột ngày hoàn thành, tức LỊCH SỬ ĐÃ HỌC. Ba thứ chịu lực trong luồng người
// dùng vừa mô tả — nhu cầu học (để suy ra số lớp phải mở), sĩ số tối thiểu/tối đa (để chạy waitlist), và
// ai là quản lý của ai (để duyệt ticket) — không có cột nào tương ứng trong file. Không thứ nào trong ba
// thứ đó tự lộ ra khi đọc file; chúng chỉ lộ ra khi so file với lời kể.
//
// Vì sao chốt bằng test chứ không bằng một phanh trong code: máy đọc được file nguồn nhưng không biết
// người dùng đã kể những gì, nên không thể tự dựng phần "thiếu so với lời kể" mà không bịa. Tầng chặn
// thật là prompt, và test này giữ cho các luật đó không âm thầm rơi mất qua một lần dọn prompt.
public class SourceAckReadbackRuleTests
{
    private const string SourceAckPromptKey = "BusinessAnalyst/source-ack.v2.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void SourceAckPrompt_RequiresExhaustiveColumnReading()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        // Luật lõi: danh mục của cột lấy từ khối thống kê, không suy từ các dòng mẫu.
        Assert.Contains("Thống kê cột", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG từ các dòng mẫu", prompt, StringComparison.Ordinal);
        Assert.Contains("chép **hết** các giá trị", prompt, StringComparison.Ordinal);

        // Cột trống 100% / một giá trị duy nhất là dữ kiện, không phải chuyện vặt được phép bỏ qua.
        Assert.Contains("Cột không mang thông tin", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAckPrompt_RequiresCrossCheckAgainstWhatTheUserSaid()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("Đối chiếu tài liệu với điều người dùng đã kể", prompt, StringComparison.Ordinal);

        // Ba câu hỏi đối chiếu: đúng file đã xin chưa, thiếu gì so với lời kể, quy mô có khớp không.
        Assert.Contains("file bạn đã xin", prompt, StringComparison.Ordinal);
        Assert.Contains("file KHÔNG có", prompt, StringComparison.Ordinal);
        Assert.Contains("Quy mô có khớp lời kể", prompt, StringComparison.Ordinal);
    }

    // "Chỗ chưa chắc" là hàng đợi câu hỏi cho các lượt phỏng vấn sau, nên mỗi mục thừa ở đây đốt một lượt
    // thật. Ca thật: BA hỏi người dùng nghiệp vụ xem 44330 là định dạng ngày gì (số ngày kiểu Excel, tự
    // quy đổi được) và xem bảng có bị lệch cột không (không lệch — cột Middle Name trống 100% nên
    // Organization trông như dính liền tên). Cả hai đều tự kiểm được, và cả hai đều là câu hỏi kỹ thuật
    // mà mục "Đối tượng người dùng" của prompt chat cấm đặt ra.
    [Fact]
    public void SourceAckPrompt_KeepsSelfCheckableItemsOutOfTheOpenQuestionList()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("CHỈ NGƯỜI DÙNG trả lời được", prompt, StringComparison.Ordinal);
        Assert.Contains("tự kiểm được hoặc tự suy ra được", prompt, StringComparison.Ordinal);
    }

    // Câu trả lời rỗng khác "sao cũng được" ở chỗ nó có chủ ngữ và động từ, nên trôi qua rất êm. Ca thật:
    // "Khi ticket ở trạng thái waitlist, Quản trị ứng dụng dựa vào tiêu chí nào để chuyển sang enroll hoặc
    // reject?" → "Quản trị ứng dụng tự quyết định" → BA ghi nhận và mở sang nhóm khác. Nhóm đó được tính
    // là đã hỏi xong, và tài liệu nhận về một quy tắc không ai hiện thực được.
    [Fact]
    public void ChatPrompt_TreatsEmptyAnswersAsSomethingToPinDown()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("CÂU TRẢ LỜI RỖNG", prompt, StringComparison.Ordinal);

        foreach (var phrase in new[] { "tự quyết định", "tùy tình hình", "linh động" })
            Assert.Contains(phrase, prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Xin tài liệu muộn không sai luật nào cả — nó chỉ lặng lẽ đắt: ca thật, người dùng nhắc tới file
    // Master List ngay lượt kể luồng chính, BA sáu lượt sau mới xin, và sáu lượt đó dùng để hỏi tay đúng
    // các cột file đã có sẵn.
    [Fact]
    public void ChatPrompt_AsksForTheSourceFileAtTheTurnItIsMentioned()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("NGAY TẠI LƯỢT ĐÓ", prompt, StringComparison.Ordinal);
    }

    // Cùng cách tìm Prompts/ như BAChatPlaybackRuleTests: ưu tiên bản copy trong bin, không có thì đi
    // ngược lên repo root.
    private static string ReadPrompt(string promptKey)
    {
        var relative = promptKey.Replace('/', Path.DirectorySeparatorChar);

        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
        if (File.Exists(fromBin))
            return File.ReadAllText(fromBin);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts", relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey + " từ " + AppContext.BaseDirectory);
    }
}

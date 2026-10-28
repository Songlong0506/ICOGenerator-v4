using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lượt ĐỌC TÀI LIỆU NGUỒN phải đọc hết bảng, phải đặt bảng cạnh lời người dùng, và phải nói ra cách hiểu
// của nó NGAY — không "Chỗ chưa chắc", không xin duyệt bản đọc, không bắt người dùng tích bảng cột.
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
//          giá trị mang vế "tự chọn".
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
// Ca thứ hai (file quản lý tủ locker, nhiều sheet) là lý do lượt này thành MỘT lượt: thiết kế cũ bắt
// người dùng tích bảng cột trước, lượt sau mới kể lại file kèm "Chỗ chưa chắc" và hỏi "hiểu vậy đúng chưa"
// — ba lượt trôi qua mà BA chưa nói được điều gì người dùng chưa biết, trong khi file đầy bằng chứng cho
// đúng nỗi đau họ vừa kể (Status để trống, mã nhân viên trống, một người nhiều tủ).
//
// Vì sao chốt bằng test chứ không bằng một phanh trong code: máy đọc được file nguồn nhưng không biết
// người dùng đã kể những gì, nên không thể tự dựng phần "thiếu so với lời kể" mà không bịa. Tầng chặn
// thật là prompt, và test này giữ cho các luật đó không âm thầm rơi mất qua một lần dọn prompt.
public class SourceAckRuleTests
{
    private const string SourceAckPromptKey = "BusinessAnalyst/source-ack.v3.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void SourceAckPrompt_RequiresExhaustiveColumnReading()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        // Luật lõi: danh mục của cột lấy từ khối thống kê, không suy từ các dòng mẫu — và quy mô lấy từ
        // khối tổng quan của CẢ tệp, không từ một sheet.
        Assert.Contains("Thống kê cột", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG suy từ các dòng mẫu", prompt, StringComparison.Ordinal);
        Assert.Contains("#### Tổng quan file", prompt, StringComparison.Ordinal);
        Assert.Contains("Đừng kể quy mô của một sheet như thể đó là cả tệp", prompt, StringComparison.Ordinal);
        // Cột trống 100% / một giá trị duy nhất là dữ kiện, không phải chuyện vặt được phép bỏ qua.
        Assert.Contains("Cột không mang thông tin", prompt, StringComparison.Ordinal);
    }

    // Đọc file như một vật thể độc lập mới là một nửa việc: phần đắt nhất là file làm BẰNG CHỨNG cho (hoặc
    // chống lại) lời kể. Khối BỐI CẢNH HỘI THOẠI chở lời kể đó; prompt phải dạy model dùng nó.
    [Fact]
    public void SourceAckPrompt_RequiresCrossCheckAgainstWhatTheUserSaid()
    {
        var ack = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("## BỐI CẢNH HỘI THOẠI", ack, StringComparison.Ordinal);
        Assert.Contains("Phát hiện: đặt tài liệu cạnh điều người dùng đã kể", ack, StringComparison.Ordinal);
        Assert.Contains("file bạn đã xin", ack, StringComparison.Ordinal);
        Assert.Contains("file KHÔNG có", ack, StringComparison.Ordinal);
        Assert.Contains("Quy mô có khớp lời kể", ack, StringComparison.Ordinal);
        // Chất lượng dữ liệu và dữ liệu nhạy cảm là phát hiện người dùng cần biết dù chưa nói tới.
        Assert.Contains("Chất lượng dữ liệu", ack, StringComparison.Ordinal);
        Assert.Contains("Dữ liệu nhạy cảm", ack, StringComparison.Ordinal);
    }

    // Toàn bộ điểm của thiết kế: lượt đọc file nói ngay cách hiểu rồi ĐI TIẾP. Ba thứ từng làm lượt này
    // trôi qua vô ích phải bị cấm đích danh, nếu không một lần dọn prompt là chúng quay lại.
    [Fact]
    public void SourceAckPrompt_UnderstandsInOneTurn_WithoutAskingForApproval()
    {
        var ack = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("KHÔNG có cụm \"Chỗ chưa chắc\"", ack, StringComparison.Ordinal);
        Assert.Contains("KHÔNG kết bằng câu xin xác nhận", ack, StringComparison.Ordinal);
        Assert.Contains("KHÔNG bắt người dùng rà hay gửi bảng cột", ack, StringComparison.Ordinal);
        Assert.Contains("ĐÚNG MỘT câu hỏi", ack, StringComparison.Ordinal);
        // Dấu vết của hình dạng cũ không được sót lại ở bất kỳ đâu trong prompt.
        Assert.DoesNotContain("Gửi bảng cột", ack, StringComparison.Ordinal);
        Assert.DoesNotContain("## LƯỢT NÀY: CHỐT PHẠM VI CỘT", ack, StringComparison.Ordinal);
    }

    // Bỏ "Chỗ chưa chắc" không có nghĩa là giấu điều chưa chắc: thứ tự kiểm thì tự kiểm (44330 là số ngày
    // kiểu Excel — ca thật BA đã hỏi người dùng nghiệp vụ đây là định dạng gì), thứ phải đoán thì nói thẳng
    // thành giả định để người dùng chỉ việc bác khi sai.
    [Fact]
    public void SourceAckPrompt_TurnsUncertaintyIntoStatedAssumptions()
    {
        var ack = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("tự quyết bằng GIẢ ĐỊNH nói thẳng ra", ack, StringComparison.Ordinal);
        Assert.Contains("mình hiểu là…", ack, StringComparison.Ordinal);
        Assert.Contains("44330", ack, StringComparison.Ordinal);
    }

    // Lượt đọc file nạp lại TOÀN BỘ nguồn của project — cố ý, vì nguồn cũ là thứ duy nhất để đối chiếu —
    // nên "kể lại tài liệu vừa gửi" chỉ có nghĩa nếu ai đó nói cho model biết file nào vừa gửi. Cơ chế gọi
    // đích danh file vừa gửi (BASourceAckPrompt.ReadbackScope); prompt phải dạy model đọc khối đó.
    [Fact]
    public void SourceAckPrompt_ReadsBackOnlyTheFilesJustSent()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("Phạm vi kể lại: CHỈ các file vừa gửi ở lượt này", prompt, StringComparison.Ordinal);
        Assert.Contains("## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY", prompt, StringComparison.Ordinal);
        Assert.Contains("toàn bộ** tài liệu nguồn của dự án", prompt, StringComparison.Ordinal);

        // Nhưng KHÔNG được biến thành "cấm nhìn nguồn cũ": điều đắt nhất của một lô upload thường nằm đúng
        // ở chỗ NỐI giữa file mới và file cũ.
        Assert.Contains("**nối** giữa file vừa gửi và nguồn cũ", prompt, StringComparison.Ordinal);

        // Và `sourceNotes` KHÔNG bị bó theo phạm vi đó — nó là chỗ cất ảnh thành chữ, không phải chỗ người
        // dùng đọc. Bó theo là mất VĨNH VIỄN nội dung ảnh của một nguồn cũ vừa được gửi kèm lần đầu.
        Assert.Contains("KHÔNG bị bó theo phạm vi kể lại", prompt, StringComparison.Ordinal);
    }

    // Câu trả lời rỗng khác "sao cũng được" ở chỗ nó có chủ ngữ và động từ, nên trôi qua rất êm. Ca thật:
    // "Khi ticket ở trạng thái waitlist, Quản trị ứng dụng dựa vào tiêu chí nào để chuyển sang enroll hoặc
    // reject?" → "Quản trị ứng dụng tự quyết định" → BA ghi nhận và mở sang nhóm khác.
    [Fact]
    public void ChatPrompt_TreatsEmptyAnswersAsSomethingToPinDown()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("CÂU TRẢ LỜI RỖNG", prompt, StringComparison.Ordinal);

        foreach (var phrase in new[] { "tự quyết định", "tùy tình hình", "linh động" })
            Assert.Contains(phrase, prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Xin tài liệu muộn không sai luật nào cả — nó chỉ lặng lẽ đắt: ca thật, người dùng nhắc tới file
    // Master List ngay lượt kể luồng chính, BA sáu lượt sau mới xin.
    [Fact]
    public void ChatPrompt_AsksForTheSourceFileAtTheTurnItIsMentioned()
    {
        Assert.Contains("NGAY TẠI LƯỢT ĐÓ", ReadPrompt(ChatPromptKey), StringComparison.Ordinal);
    }

    // Đặt các con số cạnh nhau bắt được cách hiểu sai với giá gần bằng không. Ca thật: Item Status có
    // Active (219) và Complete Date có giá trị ở đúng 219/262 dòng — "Active" nhiều khả năng là ĐÃ HỌC
    // XONG chứ không phải "nội dung còn hiệu lực". Cùng phép đó bắt "tên có mà mã nhân viên trống".
    [Fact]
    public void SourceAckPrompt_ReadsColumnsAgainstEachOther()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("Đặt các con số CẠNH NHAU", prompt, StringComparison.Ordinal);
        Assert.Contains("cùng số dòng có giá trị", prompt, StringComparison.Ordinal);
        Assert.Contains("lệch số giá trị phân biệt", prompt, StringComparison.Ordinal);
        Assert.Contains("Cột tên có giá trị nhiều hơn cột mã", prompt, StringComparison.Ordinal);
        // Cột chở một quy tắc người dùng đã nói là mối nối đắt nhất giữa file và lời kể.
        Assert.Contains("Cột chở một quy tắc người dùng ĐÃ NÓI", prompt, StringComparison.Ordinal);
    }

    // Text bóc từ file được nạp làm "dữ liệu mẫu thật" cho bước sinh spec, và POC seed màn hình bằng đúng
    // các cột đó. Không gọi tên các cột của HỆ CŨ thì người dùng mở demo ra thấy "Revision Number" nằm như
    // một trường của app mới. Cột dẫn xuất (Days Rem) khó thấy hơn hẳn vì nó đọc lên như dữ kiện thật.
    [Fact]
    public void Prompts_SeparateLegacyExportColumnsFromTheNewApp()
    {
        var ack = ReadPrompt(SourceAckPromptKey);
        Assert.Contains("Cột của HỆ CŨ", ack, StringComparison.Ordinal);
        Assert.Contains("Cột DẪN XUẤT", ack, StringComparison.Ordinal);
        Assert.Contains("Days Rem", ack, StringComparison.Ordinal);
        Assert.Contains("đây là chỗ DUY NHẤT xử lý chuyện đó", ack, StringComparison.Ordinal);

        // Phạm vi cột chốt bằng BẢNG CỘT ở lượt đọc file, nên việc của prompt chat là "đừng hỏi lại".
        var chat = ReadPrompt(ChatPromptKey);
        Assert.Contains("PHẠM VI CỘT đã chốt bằng BẢNG CỘT", chat, StringComparison.Ordinal);
        Assert.Contains("Bảng cột của … đã CHỐT", chat, StringComparison.Ordinal);
        Assert.Contains("KHÔNG hỏi lại \"cột nào anh/chị dùng\"", chat, StringComparison.Ordinal);
    }

    // Bảng cột do BA chốt và hệ thống lưu luôn — nên nó phải điền sẵn và tự quyết, không phải một biểu mẫu
    // 18 dòng trống chờ người dùng giải nghĩa.
    [Fact]
    public void SourceAckPrompt_FillsAndDecidesTheColumnTableItself()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("PHẠM VI CỘT do bạn chốt", prompt, StringComparison.Ordinal);
        Assert.Contains("Hệ thống LƯU LUÔN bảng này", prompt, StringComparison.Ordinal);
        Assert.Contains("`meaning` phải ĐIỀN SẴN", prompt, StringComparison.Ordinal);
        Assert.Contains("`used` là quyết định của bạn", prompt, StringComparison.Ordinal);
        Assert.Contains("chép ĐÚNG tên trong hàng tiêu đề", prompt, StringComparison.Ordinal);
        Assert.Contains("KHỚP với cách bạn hiểu cột đó ở mọi chỗ khác", prompt, StringComparison.Ordinal);
        // Và bảng phải có mặt trong khối định dạng, nếu không model không bao giờ xuất trường này.
        Assert.Contains("\"columns\"", prompt, StringComparison.Ordinal);
    }

    // Xin file là lời nhờ HÀNH ĐỘNG, không phải câu hỏi: người dùng đọc xong thì đi tìm file, và mọi thứ khác
    // trong lượt bị nuốt mất.
    [Fact]
    public void ChatPrompt_AsksForTheFileOnATurnOfItsOwn()
    {
        var chat = ReadPrompt(ChatPromptKey);

        Assert.Contains("lượt xin file phải ĐỨNG MỘT MÌNH", chat, StringComparison.Ordinal);
        Assert.Contains("KHÔNG gộp lời **xin file** với một câu hỏi khác", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPrompt_BansColumnByColumnInterrogation()
    {
        var chat = ReadPrompt(ChatPromptKey);

        Assert.Contains("TUYỆT ĐỐI KHÔNG đi từng cột một", chat, StringComparison.Ordinal);
        Assert.Contains("giải nghĩa từng cột", chat, StringComparison.Ordinal);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được model có tuân hay không.
    [Fact]
    public void GoldenSet_CoversTheSourceAckRules()
    {
        var scenarios = EvalScenariosSeedData.Build();

        var sourceAck = scenarios.Where(s => s.PromptKey == SourceAckPromptKey).Select(s => s.Criteria).ToList();
        Assert.NotEmpty(sourceAck);
        // Prompt kể lại đã gỡ — không scenario nào được trỏ tới nó.
        Assert.DoesNotContain(scenarios, s => s.PromptKey == "BusinessAnalyst/source-readback.v1.md");

        // Ba thứ bị cấm của lượt đọc file đều phải có điểm chấm.
        Assert.Contains(sourceAck, c => c.Contains("TRƯỢT nếu message có cụm \"Chỗ chưa chắc\"", StringComparison.Ordinal));
        Assert.Contains(sourceAck, c => c.Contains("câu xin xác nhận bản đọc", StringComparison.Ordinal));
        Assert.Contains(sourceAck, c => c.Contains("ĐÚNG MỘT câu hỏi", StringComparison.Ordinal));
        // Giá trị chỉ có trong khối thống kê, không có trong dòng mẫu — đúng chỗ bản đọc thật đã trượt.
        Assert.Contains(sourceAck, c => c.Contains("OPT", StringComparison.Ordinal));
        Assert.Contains(sourceAck, c => c.Contains("Days Rem", StringComparison.Ordinal)
                                        && c.Contains("DẪN XUẤT", StringComparison.Ordinal));
        Assert.Contains(sourceAck, c => c.Contains("219", StringComparison.Ordinal)
                                        && c.Contains("ĐÃ HỌC XONG", StringComparison.Ordinal));
        Assert.Contains(sourceAck, c => c.Contains("meaning ĐIỀN SẴN", StringComparison.Ordinal));
        // Phạm vi kể lại: nguồn cũ nằm ngay trong ngữ cảnh, nên chỉ prompt thôi không chứng minh được model
        // chịu bỏ qua chúng — chỗ đo là đây.
        Assert.Contains(sourceAck, c => c.Contains("chỉ kể lại", StringComparison.OrdinalIgnoreCase)
                                        && c.Contains("chỗ NỐI", StringComparison.Ordinal));
        // Tệp nhiều sheet: quy mô cả tệp, và phát hiện nối với nỗi đau người dùng.
        Assert.Contains(sourceAck, c => c.Contains("Tổng quan file", StringComparison.Ordinal)
                                        && c.Contains("data không được update", StringComparison.Ordinal));

        var chat = scenarios.Where(s => s.PromptKey == ChatPromptKey).Select(s => s.Criteria).ToList();
        Assert.Contains(chat, c => c.Contains("Assignment Type nghĩa là gì?", StringComparison.Ordinal));
        Assert.Contains(chat, c => c.Contains("hỏi lại phạm vi cột", StringComparison.Ordinal));
        Assert.Contains(chat, c => c.Contains("Lượt xin file phải ĐỨNG MỘT MÌNH", StringComparison.Ordinal));
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

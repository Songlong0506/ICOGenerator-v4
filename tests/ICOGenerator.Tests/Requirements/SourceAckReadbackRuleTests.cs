using ICOGenerator.Data;
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
    private const string SourceAckPromptKey = "BusinessAnalyst/source-ack.v3.md";
    private const string ReadbackPromptKey = "BusinessAnalyst/source-readback.v1.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void ReadbackPrompt_RequiresExhaustiveColumnReading()
    {
        var prompt = ReadPrompt(ReadbackPromptKey);

        // Luật lõi: danh mục của cột lấy từ khối thống kê, không suy từ các dòng mẫu.
        Assert.Contains("Thống kê cột", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG suy từ các dòng mẫu", prompt, StringComparison.Ordinal);
        Assert.Contains("chép ĐỦ các giá trị kèm số dòng", prompt, StringComparison.Ordinal);

        // Cột trống 100% / một giá trị duy nhất là dữ kiện, không phải chuyện vặt được phép bỏ qua —
        // luật này sống ở lượt dựng bảng, nơi cột đó phải có một dòng và một ô ý nghĩa nói thẳng ra.
        Assert.Contains("Cột không mang thông tin", ReadPrompt(SourceAckPromptKey), StringComparison.Ordinal);
    }

    [Fact]
    public void BothPrompts_RequireCrossCheckAgainstWhatTheUserSaid()
    {
        // Bảng tính: phần đối chiếu nằm ở lượt KỂ LẠI (sau khi chốt cột) — ba câu hỏi đối chiếu đủ cả.
        var readback = ReadPrompt(ReadbackPromptKey);
        Assert.Contains("Đối chiếu file với điều người dùng đã kể", readback, StringComparison.Ordinal);
        Assert.Contains("file bạn đã xin", readback, StringComparison.Ordinal);
        Assert.Contains("file KHÔNG có", readback, StringComparison.Ordinal);
        Assert.Contains("Quy mô có khớp lời kể", readback, StringComparison.Ordinal);

        // Word/PDF/ảnh không có bảng cột nên bản đọc lại của chúng vẫn nằm ở lượt mở tài liệu — phần đối
        // chiếu phải đi theo, nếu không loại nguồn đó mất trắng chốt chặn này.
        var ack = ReadPrompt(SourceAckPromptKey);
        Assert.Contains("Đối chiếu tài liệu với điều người dùng đã kể", ack, StringComparison.Ordinal);
        Assert.Contains("file bạn đã xin", ack, StringComparison.Ordinal);
        Assert.Contains("Quy mô có khớp lời kể", ack, StringComparison.Ordinal);
    }

    // THỨ TỰ là toàn bộ điểm của thay đổi này: bảng cột trước, bản đọc lại sau. Lượt upload vừa bày bảng
    // vừa kể lại cả file là dựng "Chỗ chưa chắc" trên những cột người dùng sắp bỏ tích ngay bên dưới — mỗi
    // mục thừa ở đó đốt một lượt phỏng vấn thật — và nếu họ gửi nhầm file thì cả bản đọc là công cốc, mà
    // vẫn đủ hợp lý để được bấm "Đúng rồi".
    [Fact]
    public void SourceAckPrompt_DefersTheSpreadsheetReadbackUntilTheColumnScopeIsSettled()
    {
        var ack = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("Vì sao bản đọc lại của BẢNG TÍNH phải đứng SAU bảng cột", ack, StringComparison.Ordinal);
        // Hai hình dạng của lượt, và luật "hình dạng do hệ thống chọn" — model không tự đoán được file nào
        // đang chờ chốt cột vì nó thấy text của MỌI nguồn trong project.
        Assert.Contains("## LƯỢT NÀY: CHỐT PHẠM VI CỘT", ack, StringComparison.Ordinal);
        Assert.Contains("## LƯỢT NÀY: BẢN ĐỌC LẠI", ack, StringComparison.Ordinal);
        Assert.Contains("CẤM trong lượt này", ack, StringComparison.Ordinal);

        // Và nửa sau phải nói rõ nó LÀ bản đọc lại bị dời tới, nếu không nó thành một lượt chào hỏi.
        var readback = ReadPrompt(ReadbackPromptKey);
        Assert.Contains("KỂ LẠI", readback, StringComparison.Ordinal);
        Assert.Contains("Chỗ chưa chắc", readback, StringComparison.Ordinal);
        // Lượt này không hỏi khai thác: thẻ hỏi gộp nuốt mất hai chip xác nhận.
        Assert.Contains("`questions`: mảng RỖNG", readback, StringComparison.Ordinal);
    }

    // "Chỗ chưa chắc" là hàng đợi câu hỏi cho các lượt phỏng vấn sau, nên mỗi mục thừa ở đây đốt một lượt
    // thật. Ca thật: BA hỏi người dùng nghiệp vụ xem 44330 là định dạng ngày gì (số ngày kiểu Excel, tự
    // quy đổi được) và xem bảng có bị lệch cột không (không lệch — cột Middle Name trống 100% nên
    // Organization trông như dính liền tên). Cả hai đều tự kiểm được, và cả hai đều là câu hỏi kỹ thuật
    // mà mục "Đối tượng người dùng" của prompt chat cấm đặt ra.
    [Fact]
    public void Prompts_KeepSelfCheckableItemsOutOfTheOpenQuestionList()
    {
        Assert.Contains("CHỈ NGƯỜI DÙNG trả lời được", ReadPrompt(SourceAckPromptKey), StringComparison.Ordinal);
        Assert.Contains("tự kiểm được hoặc tự suy ra được", ReadPrompt(SourceAckPromptKey), StringComparison.Ordinal);

        // Cùng luật ở lượt kể lại bảng tính, kèm đúng ca thật đã trượt: hỏi người dùng nghiệp vụ xem 44330
        // là định dạng ngày gì, trong khi đó là số ngày kiểu Excel và tự quy đổi được.
        var readback = ReadPrompt(ReadbackPromptKey);
        Assert.Contains("CHỈ NGƯỜI DÙNG trả lời được", readback, StringComparison.Ordinal);
        Assert.Contains("44330", readback, StringComparison.Ordinal);
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

    // Giữa hai thái cực đều hỏng: hỏi giải nghĩa CẢ BẢNG (18 cột thành 18 việc tồn, người dùng bỏ dở, và
    // bị hỏi "Last Name nghĩa là gì" đọc lên đúng như "tôi không đọc được file của anh/chị"), và KHÔNG hỏi
    // gì — đúng chuyện đã xảy ra với Assignment Type REQ/MAN/OPT, cột mã hóa "bắt buộc / tự chọn" mà người
    // dùng nói ngay câu đầu tiên. Luật là CHỌN cột, rồi nêu dưới dạng đề xuất để họ gật/lắc.
    [Fact]
    public void ReadbackPrompt_SelectsWhichColumnsAreWorthAsking_AndProposesAReading()
    {
        var readback = ReadPrompt(ReadbackPromptKey);

        Assert.Contains("đừng đi qua từng cột giải nghĩa lại", readback, StringComparison.Ordinal);
        Assert.Contains("Cột chở một quy tắc người dùng ĐÃ NÓI", readback, StringComparison.Ordinal);
        Assert.Contains("dưới dạng ĐỀ XUẤT, không phải câu hỏi trống", readback, StringComparison.Ordinal);

        // Cùng mối nối đó phải có ở lượt dựng bảng, vì nó quyết định ô ý nghĩa và ô tích của đúng cột chở
        // yêu cầu lõi ("bắt buộc / tự chọn" → Assignment Type REQ/MAN/OPT).
        Assert.Contains("Cột chở một quy tắc người dùng ĐÃ NÓI", ReadPrompt(SourceAckPromptKey), StringComparison.Ordinal);
    }

    // Text bóc từ file được nạp làm "dữ liệu mẫu thật" cho bước sinh spec, và POC seed màn hình bằng đúng
    // các cột đó (RealSampleDataReader → PocSampleDataCheck). Không gọi tên các cột của HỆ CŨ thì người dùng
    // mở demo ra thấy "Revision Number" nằm như một trường của app mới.
    [Fact]
    public void Prompts_SeparateLegacyExportColumnsFromTheNewApp()
    {
        Assert.Contains("Cột của HỆ CŨ", ReadPrompt(SourceAckPromptKey), StringComparison.Ordinal);

        // Phạm vi cột nay chốt bằng BẢNG CỘT ở lượt đọc file, nên việc của prompt chat đổi chiều: không
        // phải "nhớ hỏi" mà là "đừng hỏi lại thứ người dùng vừa tích từng dòng".
        var chat = ReadPrompt(ChatPromptKey);
        Assert.Contains("PHẠM VI CỘT đã chốt bằng BẢNG CỘT", chat, StringComparison.Ordinal);
        Assert.Contains("đã được NGƯỜI DÙNG CHỐT", chat, StringComparison.Ordinal);
        Assert.Contains("KHÔNG hỏi lại \"cột nào anh/chị dùng\"", chat, StringComparison.Ordinal);
    }

    // BẢNG CỘT chỉ chữa được khiếm khuyết của hàng chip (chip chỉ nêu tập con do BA chọn, cột không lên
    // chip bị loại mà người dùng không thấy để phản đối) nếu nó KHÔNG rơi vào thái cực bên kia: một bảng
    // 18 dòng với ô ý nghĩa để trống là bắt người dùng nghiệp vụ giải nghĩa 18 cột — đúng thứ mục "Cột nào
    // đáng đưa vào Chỗ chưa chắc" cấm, và đọc lên đúng như "tôi chưa mở file của anh/chị".
    [Fact]
    public void SourceAckPrompt_PreFillsTheColumnTable_InsteadOfAskingUserToExplainEveryColumn()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("BẢNG CỘT để người dùng tích", prompt, StringComparison.Ordinal);
        Assert.Contains("`meaning` phải ĐIỀN SẴN", prompt, StringComparison.Ordinal);
        // Ô tích cũng do BA đề xuất: bảng toàn ô trống bắt người dùng tự quyết phạm vi kỹ thuật.
        Assert.Contains("`used` là ĐỀ XUẤT của bạn", prompt, StringComparison.Ordinal);
        // Tên cột phải chép đúng file — tên lệch bị bộ chuẩn hoá loại khỏi bảng.
        Assert.Contains("chép ĐÚNG tên trong hàng tiêu đề", prompt, StringComparison.Ordinal);
        // Và bảng phải có mặt trong khối định dạng, nếu không model không bao giờ xuất trường này.
        Assert.Contains("\"columns\"", prompt, StringComparison.Ordinal);
    }

    // Khối thống kê cho số dòng có giá trị của MỌI cột, nên đặt vài con số cạnh nhau là bắt được cách hiểu
    // sai với giá gần bằng không. Ca thật: Item Status có Active (219) và Complete Date có giá trị ở đúng
    // 219/262 dòng — trùng khít như vậy nói rằng "Active" nhiều khả năng là ĐÃ HỌC XONG chứ không phải "nội
    // dung còn hiệu lực". Bản đọc thật nêu cả hai con số cách nhau ba dòng mà không nối chúng lại, rồi chốt
    // nhầm sang nghĩa thứ hai. Cột đó quyết định file kể "ai đã học" hay "nội dung nào còn dùng", tức quyết
    // định file có suy ra được nhu cầu học hay không — đầu vào của toàn bộ việc tính số lớp.
    [Fact]
    public void ReadbackPrompt_ReadsColumnsAgainstEachOther_BeforeSettlingOnAMeaning()
    {
        var prompt = ReadPrompt(ReadbackPromptKey);

        Assert.Contains("Đọc các cột CẠNH NHAU", prompt, StringComparison.Ordinal);
        Assert.Contains("cùng số dòng có giá trị", prompt, StringComparison.Ordinal);
        // Cột mã và cột tên lệch số giá trị phân biệt (134 mã / 136 tiêu đề) ⇒ mã không phải khóa như tưởng.
        Assert.Contains("số giá trị phân biệt lệch nhau", prompt, StringComparison.Ordinal);
    }

    // Hai chỗ cùng hiển thị trên một màn hình nói ngược nhau thì không ô tích nào phân xử được: bản đọc ghi
    // "trạng thái giao/hoàn thành" trong khi meaning của Item Status ghi "trạng thái nội dung", và dù người
    // dùng tích thế nào thì một trong hai cách hiểu vẫn chảy tiếp vào Product Brief.
    [Fact]
    public void SourceAckPrompt_KeepsTheColumnTableConsistentWithTheReadback()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("KHỚP với cách bạn hiểu cột đó ở mọi chỗ khác", prompt, StringComparison.Ordinal);
    }

    // Cột hệ cũ không chỉ là cột hạ tầng. `Days Rem` (= Required Date trừ ngày hệ cũ xuất file) đọc lên như
    // một dữ kiện nghiệp vụ thật nên trôi qua rất êm — nhưng app mới tính lại được bất cứ lúc nào, còn con số
    // trong file thì đứng yên từ ngày xuất. Tích nó là seed lên màn hình POC một con số vĩnh viễn sai, cùng
    // hạng thiệt hại với Revision Number nhưng khó thấy hơn hẳn.
    [Fact]
    public void SourceAckPrompt_TreatsDerivedColumnsAsLegacyToo()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("Cột DẪN XUẤT", prompt, StringComparison.Ordinal);
        Assert.Contains("Days Rem", prompt, StringComparison.Ordinal);
    }

    // Bảng cột đã đi qua ĐỦ mọi cột kèm ô ý nghĩa SỬA ĐƯỢC, nên một bản đọc đi qua nốt 18 cột trong văn xuôi
    // là lặp lại cùng nội dung ở dạng không sửa được. Ca thật: bản đọc trải cả "Revision Number có 3 giá trị:
    // 1 (218), 3 (21), 2 (18)" và "Preferred Time zone gồm Asia/Saigon (212), CET (50)" — người dùng nghiệp
    // vụ làm đúng thứ phải làm với một bức tường số là đọc lướt rồi bấm "Đúng rồi", và lượt bắt lỗi đầu vào
    // mất trắng. Luật này KHÔNG cho phép bỏ bớt bản đọc: nó chỉ chuyển phần giải nghĩa cột sang bảng, còn
    // tổng quan, cột chở quy tắc và phần đối chiếu với lời kể vẫn phải nguyên.
    [Fact]
    public void ReadbackPrompt_DoesNotRepeatTheColumnTableInsideTheReadback()
    {
        var readback = ReadPrompt(ReadbackPromptKey);

        Assert.Contains("Chỉ nói về CỘT ĐÃ TÍCH", readback, StringComparison.Ordinal);
        // Vẫn phải giữ những thứ bảng cột không chở được — nếu không, luật này biến thành cái cớ đọc qua loa.
        Assert.Contains("Các cột chở một QUY TẮC nghiệp vụ", readback, StringComparison.Ordinal);

        // Và phạm vi cột chỉ được xử lý ở MỘT chỗ — chính cái bảng người dùng vừa tích từng dòng. Nêu lại
        // trong "Chỗ chưa chắc" thì nó thành việc tồn, rồi lượt phỏng vấn sau đi hỏi lại đúng thứ đó.
        Assert.Contains("KHÔNG** nêu lại chuyện **phạm vi cột**", readback, StringComparison.Ordinal);
        Assert.Contains("đây là chỗ DUY NHẤT xử lý chuyện đó", ReadPrompt(SourceAckPromptKey), StringComparison.Ordinal);
    }

    // Lượt có BẢNG CỘT thì BAChatService bỏ hàng chip "Đúng rồi / Chưa đúng" (chip bấm là gửi ngay, để cả
    // hai cùng sống thì một cú bấm nhầm gửi mất lượt trước khi người dùng kịp tích xong bảng). Nhưng prompt
    // vẫn bắt kết bằng câu hỏi đóng "Mình hiểu vậy đã đúng chưa ạ?" ⇒ trên màn hình là một câu hỏi KHÔNG CÓ
    // nút trả lời: người dùng đi tìm nút "Đúng rồi" không thấy, còn việc thật sự phải làm — rà bảng rồi bấm
    // "Gửi bảng cột" — thì câu kết không hề nhắc tới. Câu kết phải đổi theo đường trả lời đang hiện, nên hai
    // ca phải cùng nằm trong prompt: bỏ ca Word/PDF/ảnh đi thì lượt không có bảng mất luôn câu hỏi đóng, mà
    // đó mới là chỗ hai chip là đường trả lời DUY NHẤT.
    [Fact]
    public void SourceAckPrompt_ClosesTheTurnWithWhicheverAnswerControlIsOnScreen()
    {
        var prompt = ReadPrompt(SourceAckPromptKey);

        Assert.Contains("Câu kết phải chỉ vào ĐÚNG cái nút đang có trên màn hình", prompt, StringComparison.Ordinal);
        // Ca có bảng: câu kết mời rà và gửi bảng, không phải hỏi đóng.
        Assert.Contains("Gửi bảng cột", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG CÓ nút trả lời", prompt, StringComparison.Ordinal);
        // Ca không có bảng (Word/PDF/ảnh): câu hỏi đóng + hai chip giữ nguyên.
        Assert.Contains("BẢN ĐỌC LẠI (Word / PDF / ảnh)", prompt, StringComparison.Ordinal);
        Assert.Contains("câu hỏi đóng", prompt, StringComparison.Ordinal);
    }

    // Xin file là lời nhờ HÀNH ĐỘNG, không phải câu hỏi: người dùng đọc xong thì đi tìm file, và mọi thứ khác
    // trong lượt bị nuốt mất. Ca thật đã gặp trên màn hình: BA vừa xin file Master List vừa hỏi "hiện nay
    // việc lập kế hoạch và tính số lớp được thực hiện như thế nào và điểm khó chịu nhất là gì?" — người dùng
    // đính kèm file rồi đáp đúng một dòng ("làm thủ công, tự tính tay thường bị sai sót, data không đồng
    // bộ"), tức chỉ chạm vế điểm khó chịu. Các BƯỚC của quy trình hiện tại không bao giờ được kể, mà nhóm
    // "Quy trình hiện tại & điểm khó" vẫn được tính là đã hỏi xong nên BA không quay lại nữa.
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

    // Nhật ký "Điều đã chốt" là bộ nhớ dài hạn của cuộc phỏng vấn, và bước soạn tài liệu — vốn bị CẤM tự
    // giả định — coi mỗi dòng ở đây là điều người dùng đã duyệt. Nên một dòng ghi DƯ vì BA gộp hai điều vào
    // một lượt không còn cổng nào chặn lại: BA các lượt sau thấy điểm đó đã chốt nên không hỏi lại, và tài
    // liệu chép thẳng nó ra. Đó đúng là ca thật ở lượt chốt "duyệt theo quý".
    [Fact]
    public void DecisionLogPrompt_OnlyRecordsThePartTheUserActuallyAnswered()
    {
        var prompt = ReadPrompt("BusinessAnalyst/decision-log.v1.md");

        Assert.Contains("chỉ ghi điều họ đã trả lời", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KHÔNG phải lời người dùng", prompt, StringComparison.Ordinal);
        Assert.Contains("bao trùm", prompt, StringComparison.Ordinal);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được model có tuân hay không. Trước thay đổi này cả
    // source-ack lẫn decision-log đều KHÔNG có scenario nào, dù comment đầu EvalScenariosSeedData nói mọi
    // template đo được bằng text đều phải có — xoá chúng đi là các luật trên mất chỗ chấm điểm.
    [Fact]
    public void GoldenSet_CoversTheReadbackAndDecisionLogRules()
    {
        var scenarios = EvalScenariosSeedData.Build();

        var sourceAck = scenarios.Where(s => s.PromptKey == SourceAckPromptKey).Select(s => s.Criteria).ToList();
        var readback = scenarios.Where(s => s.PromptKey == ReadbackPromptKey).Select(s => s.Criteria).ToList();
        Assert.NotEmpty(sourceAck);
        Assert.NotEmpty(readback);
        // Giá trị chỉ có trong khối thống kê, không có trong dòng mẫu — đúng chỗ bản đọc thật đã trượt.
        Assert.Contains(readback, c => c.Contains("OPT", StringComparison.Ordinal));
        Assert.Contains(readback, c => c.Contains("Chỗ chưa chắc", StringComparison.Ordinal));
        // Hai lượt có hai cái bẫy NGƯỢC NHAU, nên cả hai chiều đều phải có điểm chấm: lượt bày bảng trượt
        // vì kể quá nhiều (bản đọc lại + "Chỗ chưa chắc" dựng trên cột người dùng sắp bỏ tích), lượt kể
        // lại trượt vì kể quá ít hoặc kể lại chính những cột vừa bị bỏ.
        Assert.Contains(sourceAck, c => c.Contains("TRƯỢT nếu message có cụm \"Chỗ chưa chắc\"", StringComparison.Ordinal));
        Assert.Contains(readback, c => c.Contains("TRƯỢT nếu message nhắc tới Days Rem", StringComparison.Ordinal));

        var decisionLog = scenarios.Where(s => s.PromptKey == "BusinessAnalyst/decision-log.v1.md")
            .Select(s => s.Criteria).ToList();
        Assert.NotEmpty(decisionLog);
        Assert.Contains(decisionLog, c => c.Contains("chưa được xác nhận", StringComparison.OrdinalIgnoreCase));

        // Bảng cột phải có chỗ chấm điểm ở CẢ HAI đầu của nó, vì hỏng ở đầu nào cũng mất trắng:
        // model điền bảng (ý nghĩa phải viết sẵn), và model chat KHÔNG hỏi lại bảng đã chốt.
        Assert.Contains(sourceAck, c => c.Contains("meaning ĐIỀN SẴN", StringComparison.Ordinal)
                                        || c.Contains("meaning viết sẵn", StringComparison.Ordinal));

        var chat = scenarios.Where(s => s.PromptKey == ChatPromptKey).Select(s => s.Criteria).ToList();
        Assert.Contains(chat, c => c.Contains("Assignment Type nghĩa là gì?", StringComparison.Ordinal));
        Assert.Contains(chat, c => c.Contains("hỏi lại phạm vi cột", StringComparison.Ordinal));

        // Ba luật thêm sau khi soát lại một cuộc phỏng vấn thật, cả ba đều là chỗ prompt định hướng được mà
        // máy không chặn được — nên chúng chỉ tồn tại nếu có điểm chấm.
        Assert.Contains(chat, c => c.Contains("Lượt xin file phải ĐỨNG MỘT MÌNH", StringComparison.Ordinal));
        // Cột dẫn xuất được quyết ở lượt bày bảng (ô tích), còn chỗ trùng khít 219/262 là chuyện của lượt
        // kể lại — mỗi luật nằm ở đúng lượt mà nó thật sự chi phối.
        Assert.Contains(sourceAck, c => c.Contains("Days Rem", StringComparison.Ordinal)
                                        && c.Contains("DẪN XUẤT", StringComparison.Ordinal));
        Assert.Contains(readback, c => c.Contains("219", StringComparison.Ordinal)
                                       && c.Contains("ĐÃ HỌC XONG", StringComparison.Ordinal));
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

using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Văn xuôi của BA quay lại trong bản kể của một BẢNG vẫn là lời BA.
//
// Ca thật (dự án JD Library 1), hỏng theo bốn nhịp nối nhau:
//
//   Người dùng (lượt 7): "manager tự tạo JD cho orgUnit của mình rồi submit, JD sẽ được đưa qua cho HRBP
//                         của phòng nhân sự để verify … HRBP verify thi JD sẽ được chuyển qua cho HoD của
//                         manager đó approve"
//   Người dùng (lượt 15): tự tay rà và gửi BẢNG LUỒNG với đúng bốn bước đó.
//   BA (lượt 46):        bày BẢNG ĐỐI TƯỢNG, ô mô tả của JD ghi "Mô tả công việc được Manager tạo, kiểm
//                        tra, verify và approve trước khi dùng để gán cho nhân viên".
//   Người dùng (lượt 47): sửa các Ô (Degree thành chọn nhiều, bốn danh mục thành "ứng dụng tự quản lý")
//                         rồi bấm "Gửi bảng đối tượng" — ô mô tả nằm cạnh tên đối tượng như một cái nhãn
//                         xám nên không ai rà nó, và nó đi theo bản kể vào một lượt mang VAI NGƯỜI DÙNG.
//   BA (lượt 48):        "trong bảng luồng anh/chị đã chốt, HRBP phòng Nhân sự thực hiện verify và HoD của
//                         Manager thực hiện approve; nhưng phần mô tả JD vừa gửi lại ghi Manager thực hiện
//                         verify và approve. Luồng nào đúng với thực tế ạ?"
//
// Bốn tầng thiệt hại:
//
//  1. Không có mâu thuẫn nào: vế "Manager verify và approve" là câu của CHÍNH BA, hai vế không cùng nguồn.
//     Mục "Hai vế phải cùng là lời NGƯỜI DÙNG" chỉ liệt kê ba thứ cấm (hằng số ngữ cảnh, câu "mình ghi
//     nhận" của BA, suy luận từ hai thứ đó) — bản kể của bảng lọt qua vì nó về tới trong vai người dùng.
//  2. Lượt gỡ mâu thuẫn phải đứng MỘT MÌNH, nên lượt 47 — nơi người dùng thật sự bổ sung sáu danh mục và
//     sửa Degree thành chọn nhiều — không được ghi nhận một câu nào.
//  3. Bộ chắt "điểm cần làm rõ" giữ mục "Chưa rõ ai thực hiện verify và approve JD" ⇒ CoveragePendingGuard
//     hạ ba dòng bản đồ xuống [MỘT PHẦN] và cổng "Write Requirement" KHÓA lại, ở đúng lượt BA vừa nói "các
//     nhóm thông tin chính đã đủ". Bộ chắt bản đồ bao phủ còn trích thẳng câu đó làm.
//  4. Câu hỏi bày hai vế NGANG NHAU cho một người vừa bấm gửi cái bảng ghi vế sai ⇒ nếu họ chọn nhầm thì
//     luồng bốn mắt do chính họ kể bị lật, và mọi tầng sau tin theo.
//
// Cơ chế chặn phần chặn được (EntityMapBuilder/ScreenScopeMapBuilder: bỏ ô mô tả khỏi bản kể, gắn nhãn
// xuất xứ trong khối ngữ cảnh — xem InterviewTableBuilderTests). Phần còn lại là prompt: BA phải viết ô mô
// tả cho đúng hình dạng ngay từ đầu, và ba bộ đọc (chat, bản đồ bao phủ, triển vọng phỏng vấn) phải thôi
// coi ô đó là lời người dùng. Test này giữ cho cả bốn quy tắc đó không âm thầm rơi mất.
public class BAChatTableCaptionRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    // Luật của ô `description`/`evidence` đi cùng đặc tả bảng đối tượng, nay ở prompt riêng của nó;
    // luật song sinh cho ô `purpose` đi cùng bảng màn hình.
    private const string EntityTablePromptKey = "BusinessAnalyst/table-entity-map.v1.md";
    private const string ScreenScopeTablePromptKey = "BusinessAnalyst/table-screen-scope.v1.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";
    private const string OutlookPromptKey = "BusinessAnalyst/interview-outlook.v3.md";

    // Tầng 1: đừng viết ra câu đó. Ô mô tả nói đối tượng LÀ GÌ; ai làm gì thuộc bảng luồng và ô "khi nào
    // chuyển vào" của từng trạng thái.
    [Fact]
    public void EntityTablePrompt_KeepsRolesAndApprovalVerbsOutOfTheEntityDescription()
    {
        var prompt = ReadPrompt(EntityTablePromptKey);

        Assert.Contains("`description` nói đối tượng LÀ GÌ", prompt, StringComparison.Ordinal);
        Assert.Contains("không có động từ của quy trình duyệt", prompt, StringComparison.Ordinal);

        // Ca thật phải còn nguyên trong prompt: BA nhận ra mình đang viết đúng câu đó thì mới dừng được.
        Assert.Contains("Manager** tạo, kiểm tra, verify và approve", prompt, StringComparison.Ordinal);

        // Cùng luật cho ô "việc của màn" — chặn một bảng mà bỏ bảng kia là để nguyên đường cũ, đổi tên ô.
        // Từ lúc mỗi bảng có prompt riêng, luật ấy phải sống ở CẢ HAI file: lượt bày bảng màn hình không
        // còn đọc thấy prompt của bảng đối tượng nữa.
        Assert.Contains("`purpose`", prompt, StringComparison.Ordinal);
        Assert.Contains("nói màn hình LÀ GÌ, không kể AI LÀM GÌ với nó",
            ReadPrompt(ScreenScopeTablePromptKey), StringComparison.Ordinal);
    }

    // Tầng 2: viết rồi thì cũng đừng đem ra chất vấn. Vế thứ tư của danh sách "không bao giờ được làm một
    // vế" — thứ mà ba vế cũ không phủ, vì bản kể về tới trong lượt mang tên người dùng.
    [Fact]
    public void ChatPrompt_BansTheBaOwnTableCaptionAsOneSideOfAConflict()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("Hai vế phải cùng là lời NGƯỜI DÙNG", prompt, StringComparison.Ordinal);
        Assert.Contains("Bốn thứ **KHÔNG bao giờ** được làm một vế", prompt, StringComparison.Ordinal);
        Assert.Contains("quay lại trong bản kể của một BẢNG", prompt, StringComparison.Ordinal);

        // Cách xử đúng khi câu mô tả lệch với hội thoại: sửa im lặng, không hỏi.
        Assert.Contains("tự sửa im lặng, KHÔNG hỏi", prompt, StringComparison.Ordinal);
    }

    // Trích dẫn thật nhưng chỉ đỡ được nửa câu vẫn cho ra một dòng ✓ trông như đã kiểm chứng — và phần không
    // ai nói chính là phần trôi đi xa nhất. Ca thật: khóa một dòng khai thêm ai verify, ai approve.
    [Fact]
    public void EntityTablePrompt_RequiresTheEvidenceToCoverTheWholeDescription()
    {
        var prompt = ReadPrompt(EntityTablePromptKey);

        Assert.Contains("phủ TRỌN câu bạn viết ở dòng đó", prompt, StringComparison.Ordinal);
        Assert.Contains("VIẾT NGẮN LẠI cho vừa trích dẫn", prompt, StringComparison.Ordinal);
    }

    // Tầng 3: bộ chắt bản đồ bao phủ. Nội dung bảng đã chốt vẫn là bằng chứng hợp lệ — nhưng chỉ các Ô, không
    // phải cái nhãn đứng cạnh tên đối tượng.
    [Fact]
    public void CoveragePrompt_DoesNotQuoteTheBaWrittenCaptionAsEvidence()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("không trích câu MÔ TẢ mà BA điền sẵn", prompt, StringComparison.Ordinal);
        Assert.Contains("BA tự đặt, chưa ai rà", prompt, StringComparison.Ordinal);
    }

    // Tầng 4: danh sách câu hỏi — chỗ mà một mục thừa không chỉ là ghi chú, nó khóa cổng thật. Luật này đi
    // cùng danh sách khi danh sách dời sang lượt chắt lọc bản đồ.
    [Fact]
    public void CoveragePrompt_DoesNotTurnTheBaWrittenCaptionIntoANewQuestion()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("không đẻ ra mâu thuẫn với chính lời người dùng", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG phải một câu hỏi mới", prompt, StringComparison.Ordinal);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự thôi chất vấn hay không.
    [Fact]
    public void GoldenSet_ScoresTheTableCaptionRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("ô mô tả do CHÍNH BA điền sẵn", StringComparison.OrdinalIgnoreCase)
            && c.Contains("hai vế không cùng nguồn", StringComparison.OrdinalIgnoreCase));
    }

    // Cùng cách tìm Prompts/ như BAChatScopeConflictRuleTests: ưu tiên bản copy trong bin, không có thì đi
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

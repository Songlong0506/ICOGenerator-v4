using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Danh sách orgUnit và danh sách nhân sự của MỌI ứng dụng trong nhà máy đồng bộ tự động từ hệ thống COMPAS.
//
// Hằng số của MÔI TRƯỜNG, cùng hạng với ranh giới phạm vi, kênh thông báo email và cách đăng nhập. Chỗ hỏng
// của nó nằm ở một luật ĐÚNG: mục "NGUỒN của dữ liệu" bắt BA hỏi dữ liệu từ đâu ra và ai quản lý từng danh
// mục — luật đó sinh ra để POC thôi dựng màn hình CRUD cho dữ liệu do nơi khác đổ sang, nhưng áp lên hai
// danh mục đã có nguồn cố định thì nó gây ra đúng cái nó sinh ra để chặn. Ca thật đã gặp trên màn hình, ba
// lượt liền: "Ai quản lý và cập nhật danh sách orgUnit trong ứng dụng?", "Ai quản lý và cập nhật thông tin
// nhân viên được dùng để gán JD?", "Danh sách OrgUnit để Manager chọn khi tạo JD được đưa vào ứng dụng bằng
// cách nào?" — người dùng phải tự gõ vào ô "Ý khác" rằng dữ liệu này app tự đồng bộ từ COMPAS. Ai bấm chip
// cho xong thì tài liệu ghi một quy trình cập nhật bằng tay không có thật.
//
// Khối ràng buộc ở Prompts/BusinessAnalyst/organization-platform.v1.md, được OrganizationContextService đính
// vào MỌI lời gọi BA (xem OrganizationContextServiceTests).
public class BAChatOrgDirectoryRuleTests
{
    private const string PlatformPromptKey = "BusinessAnalyst/organization-platform.v1.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";

    [Fact]
    public void PlatformPrompt_StatesCompasIsTheSource_AndBansTheAnswersThatDoNotExist()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("COMPAS", prompt, StringComparison.Ordinal);
        Assert.Contains("orgUnit", prompt, StringComparison.OrdinalIgnoreCase);

        // Gọi TÊN từng phương án không có thật, cùng lý do như danh sách kênh Teams/SMS/Zalo: cấm chung
        // chung "đừng hỏi nguồn" thì model vẫn thấy "ai CẬP NHẬT danh sách này" là một câu hỏi khác.
        foreach (var fake in new[] { "HRBP", "Có người tải file lên", "Nhập tay trong ứng dụng" })
        {
            Assert.Contains(fake, prompt, StringComparison.OrdinalIgnoreCase);
        }

        // Nửa "nhân sự" của cùng ràng buộc: câu này trước đây nằm ở H3 đăng nhập (danh tính đi kèm tài
        // khoản Bosch), nay về đúng chỗ nói nguồn của dữ liệu — cùng một điều bị cấm, một chỗ giữ.
        Assert.Contains("danh sách nhân viên lấy từ đâu", prompt, StringComparison.OrdinalIgnoreCase);

        // Thiệt hại cuối đường phải được gọi tên, vì nó mới là lý do ràng buộc này tồn tại.
        Assert.Contains("Quản lý OrgUnit", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Đối xứng với "email là KÊNH, không phải toàn bộ câu chuyện" và "chốt đăng nhập không có nghĩa hết
    // chuyện phải hỏi": COMPAS trả lời cây tổ chức có gì, KHÔNG trả lời ứng dụng treo thêm cái gì lên đó.
    // Đọc rộng tay thì cả nhóm "Dữ liệu / danh mục chính" bị coi là xong, mà đó là nhóm đắt nhất của một
    // ứng dụng nhân sự.
    [Fact]
    public void PlatformPrompt_KeepsTheBusinessQuestionsThatSurviveTheConstant()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("TỰ gắn thêm", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external", prompt, StringComparison.OrdinalIgnoreCase);

        // Các danh mục khác (khóa học, thiết bị…) vẫn phải hỏi nguồn như thường.
        Assert.Contains("Các danh mục KHÁC", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Cùng bài học đã phải học ở khối ranh giới phạm vi, khối kênh thông báo và khối đăng nhập.
    [Fact]
    public void PlatformPrompt_IsAProductConstant_NotSomethingTheUserSaid()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("đồng bộ từ COMPAS", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bóp méo", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Mục "NGUỒN của dữ liệu" là chỗ luật này bị kích hoạt nhầm, nên ngoại lệ phải sống ngay TRONG mục đó —
    // model đọc tới đó rồi thì không quay ngược lên khối ngữ cảnh nữa.
    [Fact]
    public void ChatPrompt_CarvesTheTwoDirectoriesOutOfTheDataSourceRule()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("Hai danh mục nằm NGOÀI mục này", prompt, StringComparison.Ordinal);
        Assert.Contains("ai quản lý và cập nhật danh sách orgUnit", prompt, StringComparison.OrdinalIgnoreCase);

        // Chủ đề thuộc file khác ⇒ prompt chat chỉ trỏ sang, không chép lại nội dung ràng buộc.
        Assert.Contains("Nền tảng đã chốt của nhà máy", prompt, StringComparison.Ordinal);
    }

    // Nửa còn lại của cùng một chốt chặn, và là nửa nguy hiểm hơn: BA bị CẤM hỏi những câu này, nên một dòng
    // bị hạ vì "chưa biết ai quản lý orgUnit" sẽ không bao giờ có đường lên lại — đúng hình dạng vòng lặp
    // câu hỏi chết mà CoverageDeadQuestionLoopTests đã phải dựng lưới một lần.
    [Fact]
    public void CoveragePrompt_NeverHoldsTheRowPartial_ForOrgUnitOrPeople()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("COMPAS", prompt, StringComparison.Ordinal);
        Assert.Contains("vòng lặp câu hỏi chết", prompt, StringComparison.OrdinalIgnoreCase);

        // Chuẩn cắt ngang "danh mục dùng để KIỂM TRA dữ liệu phải có người quản lý" cũng phải chừa hai danh
        // mục này ra — nó áp cho MỌI dòng nên một mình nó đủ để giữ dòng ở [MỘT PHẦN] vĩnh viễn.
        Assert.Contains("đã có người quản lý", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự thôi hỏi hay không.
    [Fact]
    public void GoldenSet_ScoresTheOrgDirectoryRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("COMPAS", StringComparison.OrdinalIgnoreCase)
            && c.Contains("danh sách orgUnit", StringComparison.OrdinalIgnoreCase));
    }

    // Cùng cách tìm Prompts/ như BAChatLoginRuleTests.
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

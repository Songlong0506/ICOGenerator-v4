using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Mọi ứng dụng của Bosch đều có sẵn vai Admin được khởi tạo khi roll out: có toàn bộ quyền, gán vai cho người
// dùng và chỉnh quyền của từng vai.
//
// Hằng số của MÔI TRƯỜNG, cùng hạng với kênh thông báo email, đăng nhập SSO và dữ liệu tổ chức từ COMPAS. Chỗ
// hỏng của nó nằm ở một luật từng ĐÚNG khi chưa có hằng số này: prompt chat bắt BA hỏi "hệ thống biết ai là
// <vai> bằng cách nào" cho mọi vai không suy được từ dữ liệu tổ chức. Ca thật đã gặp trên màn hình (ứng dụng
// quản lý tủ): người dùng nói "chỉ P.I.C của FCM được sửa danh sách lý do tủ hỏng", BA đốt HAI lượt hỏi "ứng
// dụng biết ai là FCM / ai là P.I.C bằng cách nào?" rồi "ai gán vai FCM / vai P.I.C?" — câu trả lời cuối
// cùng là "một admin riêng của ứng dụng", đúng điều mọi ứng dụng đều có.
//
// Khối ràng buộc ở Prompts/BusinessAnalyst/organization-platform.v1.md, được OrganizationContextService đính
// vào MỌI lời gọi BA (xem OrganizationContextServiceTests).
public class BAChatAdminRoleRuleTests
{
    private const string PlatformPromptKey = "BusinessAnalyst/organization-platform.v1.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";
    private const string PermissionMatrixPromptKey = "BusinessAnalyst/permission-matrix-draft.v1.md";

    [Fact]
    public void PlatformPrompt_StatesTheBuiltInAdmin_AndBansTheRoleAssignmentQuestions()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("Vai trò Admin", prompt, StringComparison.Ordinal);
        Assert.Contains("roll out", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gán vai trò cho từng người dùng", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉnh quyền", prompt, StringComparison.OrdinalIgnoreCase);

        // Gọi TÊN từng câu hỏi bị cấm và từng chip hỏi lại điều đã chốt — cấm chung chung "đừng hỏi ai gán
        // vai" thì model vẫn thấy "ứng dụng biết ai là FCM bằng cách nào" là một câu hỏi khác.
        Assert.Contains("ứng dụng biết ai là <vai X> bằng cách nào", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ai gán vai <X>", prompt, StringComparison.OrdinalIgnoreCase);
        foreach (var chip in new[] { "IT gán", "HR gán", "Quản lý trực tiếp gán" })
        {
            Assert.Contains(chip, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Đối xứng với "email là KÊNH, không phải toàn bộ câu chuyện": Admin chốt AI GÁN vai, không chốt ứng
    // dụng có những vai nào hay mỗi vai làm gì. Đọc rộng tay thì cả nhóm ★ «Đối tượng người dùng & vai trò»
    // bị coi là xong.
    [Fact]
    public void PlatformPrompt_KeepsTheBusinessQuestionsThatSurviveTheConstant()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("Admin KHÔNG trả lời hộ những câu nghiệp vụ còn lại", prompt, StringComparison.Ordinal);
        Assert.Contains("mỗi vai làm/xem được gì", prompt, StringComparison.OrdinalIgnoreCase);

        // Màn hình gán vai/phân quyền là của nền tảng — không dựng thành phạm vi màn hình nghiệp vụ.
        Assert.Contains("KHÔNG đưa màn hình quản lý vai trò", prompt, StringComparison.Ordinal);
    }

    // Khối đăng nhập từng bắt hỏi "vai được gán từ đâu" (kể cả vai của external) — nay câu đó đã có câu trả
    // lời mặc định, hai H3 cùng file không được nói ngược nhau.
    [Fact]
    public void PlatformPrompt_LoginBlockNoLongerAsksWhereRolesComeFrom()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.DoesNotContain("do một admin gán tay trong ứng dụng", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phải hỏi rõ vai trò của họ do ai gán", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Cùng bài học đã phải học ở các khối hằng số khác.
    [Fact]
    public void PlatformPrompt_IsAProductConstant_NotSomethingTheUserSaid()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("vai do Admin gán", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bóp méo", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Câu hỏi bị cấm từng là câu BẮT BUỘC trong checklist của prompt chat. Nó phải biến khỏi vai trò đó và
    // xuất hiện lại như một điều không được hỏi — model đọc checklist rồi thì không quay lên khối ngữ cảnh.
    [Fact]
    public void ChatPrompt_NoLongerRequiresTheWhoAssignsQuestion()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.DoesNotContain("phải hỏi ai gán", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hay có người gán trong ứng dụng?\"* là bắt buộc", prompt, StringComparison.Ordinal);
        Assert.Contains("Ai GÁN vai KHÔNG phải một câu hỏi ở đây", prompt, StringComparison.Ordinal);

        // Chủ đề thuộc file khác ⇒ prompt chat chỉ trỏ sang, không chép lại nội dung ràng buộc.
        Assert.Contains("Nền tảng đã chốt của nhà máy", prompt, StringComparison.Ordinal);
    }

    // Nửa nguy hiểm hơn của cùng chốt chặn: BA bị CẤM hỏi, nên một dòng «vai trò» bị giữ [MỘT PHẦN] vì "chưa
    // biết ai gán vai" sẽ không bao giờ có đường lên lại.
    [Fact]
    public void CoveragePrompt_NeverHoldsTheRolesRowPartial_ForWhoAssignsRoles()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("\"Ai gán vai\" KHÔNG BAO GIỜ là phần còn thiếu", prompt, StringComparison.Ordinal);
        Assert.Contains("vòng lặp câu hỏi chết", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Admin có toàn bộ quyền: ô trống ở cột Admin là bảng phân quyền nói sai về nền tảng.
    [Fact]
    public void PermissionMatrixPrompt_GrantsAdminEverything_WithoutInventingTheRole()
    {
        var prompt = ReadPrompt(PermissionMatrixPromptKey);

        Assert.Contains("Vai Admin", prompt, StringComparison.Ordinal);
        Assert.Contains("đừng vì luật này mà tự thêm vai Admin", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự thôi hỏi hay không.
    [Fact]
    public void GoldenSet_ScoresTheAdminRoleRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("Admin", StringComparison.Ordinal)
            && c.Contains("ai gán vai", StringComparison.OrdinalIgnoreCase));
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

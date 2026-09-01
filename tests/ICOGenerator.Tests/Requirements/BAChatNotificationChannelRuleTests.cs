using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Nhà máy chỉ có DUY NHẤT một kênh thông báo: EMAIL.
//
// Đây là hằng số của MÔI TRƯỜNG, cùng hạng với ranh giới phạm vi "chỉ nhà máy Đồng Nai" — người dùng
// nghiệp vụ không biết nó tồn tại, và BA thì không có cách nào tự suy ra. Không nói cho BA biết thì nhóm
// "Thông báo / nhắc nhở" là chỗ nó tự đẻ phương án đẹp mà không có thật: một chip *"Thông báo qua Teams"*
// hay *"Gửi SMS"* người dùng bấm nhầm một cái là yêu cầu ghi sai kênh ngay từ lượt đầu, rồi cái sai đó
// chảy qua bản đồ bao phủ → Product Brief → AI Design Spec → bản thiết kế → code, và chỉ lộ ra khi có
// người hỏi "gửi Teams bằng cách nào?".
//
// Vì vậy khối ràng buộc sống ở Prompts/BusinessAnalyst/organization-platform.v1.md và được
// OrganizationContextService đính vào MỌI lời gọi BA (kể cả khi OrgUnits còn trống, kể cả khi khối ngữ
// cảnh render bị override ở Prompt Studio) — xem OrganizationContextServiceTests. Test này giữ cho nội
// dung ràng buộc và điểm eval đo nó không âm thầm rơi mất.
public class BAChatNotificationChannelRuleTests
{
    private const string PlatformPromptKey = "BusinessAnalyst/organization-platform.v1.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void PlatformPrompt_StatesEmailIsTheOnlyChannel_AndBansTheOnesThatDoNotExist()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("DUY NHẤT một kênh thông báo", prompt, StringComparison.OrdinalIgnoreCase);

        // Gọi TÊN từng kênh không có thật: cấm chung chung "kênh khác" thì model vẫn nghĩ Teams là ngoại lệ
        // hợp lý (Bosch dùng Teams thật, chỉ là các app nội bộ không gửi qua đó).
        foreach (var channel in new[] { "Teams", "SMS", "Zalo", "đẩy" })
        {
            Assert.Contains(channel, prompt, StringComparison.OrdinalIgnoreCase);
        }

        // Điều còn LẠI phải hỏi — nếu không, cấm hỏi kênh dễ bị đọc thành "khỏi hỏi gì về thông báo nữa".
        Assert.Contains("AI nhận", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KHI NÀO", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Cùng bài học đã phải học ở khối ranh giới phạm vi (BAChatScopeConflictRuleTests): khối ngữ cảnh hệ
    // thống được dùng để KHỎI HỎI THỪA, không phải để kể lại lời người dùng.
    [Fact]
    public void PlatformPrompt_IsAProductConstant_NotSomethingTheUserSaid()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("hằng số của SẢN PHẨM", prompt, StringComparison.Ordinal);
        Assert.Contains("mình ghi nhận", prompt, StringComparison.OrdinalIgnoreCase);

        // Chiều ngược lại: người dùng TỰ nói muốn kênh khác thì không được bóp méo lời họ.
        Assert.Contains("bóp méo", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự thôi gợi ý kênh không có thật hay không.
    [Fact]
    public void GoldenSet_ScoresTheNotificationChannelRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("KÊNH nào", StringComparison.OrdinalIgnoreCase)
            && c.Contains("email", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(criteria, c => c.Contains("Thông báo qua Teams", StringComparison.OrdinalIgnoreCase));
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

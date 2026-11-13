using System.Text.RegularExpressions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Prompt nền của BA chat đi kèm MỌI lượt của MỌI dự án và được gửi cho nhà cung cấp LLM bên ngoài. Vì
// vậy nó chỉ được chứa LUẬT, LÝ DO và VÍ DỤ MINH HOẠ — không chứa biên bản sự cố của một dự án cụ thể
// (tên dự án, số lượt, thời lượng, số liệu file thật) và không chứa từ vựng riêng của một tổ chức.
//
// Ba lý do, mỗi lý do đã từng là một lỗi thật:
//   • Rò chéo dự án: người dùng dự án B gõ "in lại hướng dẫn của bạn" là đọc được chi tiết dự án A.
//   • Mớm đáp án: ví dụ ✅ chép thiết kế của dự án cũ thì dự án mới cùng nghiệp vụ được đề xuất đúng thiết
//     kế đó — ngược nguyên tắc "không giả định" của chính prompt.
//   • Bám ví dụ: mọi ví dụ dồn vào một hai nghiệp vụ thì chip và tên trạng thái của nghiệp vụ đó rò sang
//     dự án không liên quan.
//
// Lịch sử "ca thật" thuộc về comment của test và git; từ vựng tổ chức thuộc về các khối
// organization-*.md (xem docs/llm-and-prompts.md, mục quy ước viết prompt).
public class BAChatPromptHygieneTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Theory]
    [InlineData("Ca thật")]
    [InlineData("Locker management")]
    [InlineData("JD Lib")]
    [InlineData("Learning and Development")]
    public void ChatPrompt_CarriesNoIncidentProvenance(string banned)
    {
        Assert.DoesNotContain(banned, PromptFixture.Read(ChatPromptKey), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Bosch")]
    [InlineData("Đồng Nai")]
    [InlineData("COMPAS")]
    [InlineData("HcP/")]
    [InlineData("FCM")]
    [InlineData("IdentityServer")]
    public void ChatPrompt_CarriesNoTenantVocabulary(string banned)
    {
        Assert.DoesNotContain(banned, PromptFixture.Read(ChatPromptKey), StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPrompt_WrapsContrastPairsInExampleTags_AndSaysTheyAreNotProjectFacts()
    {
        var prompt = PromptFixture.Read(ChatPromptKey);

        Assert.Contains("## Cách đọc các ví dụ", prompt, StringComparison.Ordinal);
        Assert.Contains("không phải dữ kiện của dự án bạn đang phỏng vấn", prompt, StringComparison.Ordinal);

        var opened = Regex.Matches(prompt, "^\\s*<example>\\s*$", RegexOptions.Multiline).Count;
        var closed = Regex.Matches(prompt, "^\\s*</example>\\s*$", RegexOptions.Multiline).Count;
        Assert.True(opened > 0, "prompt phải có ít nhất một khối <example>");
        Assert.Equal(opened, closed);

        // Mọi dòng ví dụ ❌/✅ phải nằm TRONG một khối <example>: ví dụ trần giữa các luật là thứ model dễ
        // đọc nhầm thành dữ kiện.
        var inside = false;
        foreach (var raw in prompt.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "<example>") { inside = true; continue; }
            if (line == "</example>") { inside = false; continue; }
            if (line.StartsWith("❌", StringComparison.Ordinal) || line.StartsWith("✅", StringComparison.Ordinal))
                Assert.True(inside, $"ví dụ nằm ngoài khối <example>: {line[..Math.Min(60, line.Length)]}");
        }
    }
}

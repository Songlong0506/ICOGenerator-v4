using System.Reflection;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bảy nhãn nhóm trong InterviewTableGate.Groups là ĐIỀU KIỆN MỞ của sáu cổng bày bảng: cổng luồng đòi
// «Chức năng & luồng nghiệp vụ chính» đã [RÕ], cổng đối tượng đòi «Dữ liệu / danh mục chính», v.v. Chúng
// tra bản đồ bằng TIỀN TỐ, và bản đồ thì lấy danh sách nhóm thẳng từ requirement-coverage.v5.md
// (CoverageChecklist.Parse) — nên nhãn ở đây là một bản CHÉP của prompt.
//
// Bản chép đó hỏng theo kiểu tệ nhất: đổi tên một nhóm trong prompt thì Find() không khớp dòng nào, cổng
// tương ứng đọc ra "nhóm chưa [RÕ]" và im lặng KHÔNG BAO GIỜ MỞ. Không lỗi, không log, chỉ là cái bảng
// không bao giờ được bày ra và buổi phỏng vấn thiếu hẳn một chặng. CoverageGroupOpeners đã được prompt
// chốt lại theo cách này; đây là nửa còn thiếu.
public class InterviewTableGroupLabelTests
{
    [Fact]
    public void EveryGateGroupLabelMatchesAGroupInTheRealPrompt()
    {
        var labels = CoverageChecklist.Parse(CoveragePromptFixture.Read())
            .Select(x => x.Label)
            .ToList();
        Assert.Equal(12, labels.Count);

        foreach (var (name, value) in GateGroups())
        {
            // Đúng phép so mà Find() dùng: nhãn của cổng là TIỀN TỐ của nhãn trong bản đồ ("Luồng ngoại lệ"
            // khớp "Luồng ngoại lệ & trường hợp đặc biệt"). Test theo phép so khác là test một thứ khác.
            Assert.True(
                labels.Any(l => l.Trim().StartsWith(value, StringComparison.OrdinalIgnoreCase)),
                $"InterviewTableGate.Groups.{name} = «{value}» không còn khớp nhóm nào trong "
                + $"{CoverageChecklist.CoveragePromptPath}. Cổng dùng nó sẽ không bao giờ mở.");
        }
    }

    // Đọc qua reflection để một hằng số MỚI thêm vào Groups tự động được chốt luôn — kê tay danh sách ở
    // đây là dựng đúng bản chép thứ hai mà test này sinh ra để chặn.
    private static IEnumerable<(string Name, string Value)> GateGroups()
    {
        var groups = typeof(InterviewTableGate).GetNestedType("Groups", BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(groups);

        var fields = groups!
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();

        Assert.NotEmpty(fields);
        return fields.Select(f => (f.Name, (string)f.GetRawConstantValue()!));
    }
}

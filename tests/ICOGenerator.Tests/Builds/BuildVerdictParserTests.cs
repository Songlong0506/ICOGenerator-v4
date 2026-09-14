using ICOGenerator.Services.Builds;
using Xunit;

namespace ICOGenerator.Tests.Builds;

/// <summary>
/// Dòng <c>BUILD: …</c> là mối nối giữa cổng biên dịch (nơi GHI) và trang Delivery Quality (nơi ĐỌC).
/// Compiler không kiểm được mối nối này, và nó đứt trong im lặng: báo cáo chỉ đơn giản không thấy run
/// nào đo được. Vì vậy hai đầu phải đi qua cùng một cặp Format/Parse, và cặp đó phải có test.
/// </summary>
public class BuildVerdictParserTests
{
    [Theory]
    [InlineData(BuildVerdict.Pass)]
    [InlineData(BuildVerdict.Fail)]
    [InlineData(BuildVerdict.Skipped)]
    public void Format_And_Parse_Round_Trip(BuildVerdict verdict) =>
        Assert.Equal(verdict, BuildVerdictParser.Parse(BuildVerdictParser.Format(verdict)));

    // Task chạy TRƯỚC khi có cổng này: không có dòng nào để đọc. Phải là Unknown chứ không phải Fail —
    // nếu không, mọi lịch sử cũ bỗng thành "không biên dịch được".
    [Fact]
    public void Missing_Marker_Is_Unknown()
    {
        Assert.Equal(BuildVerdict.Unknown, BuildVerdictParser.Parse(null));
        Assert.Equal(BuildVerdict.Unknown, BuildVerdictParser.Parse("Đã sinh 12 file, chạy bằng dotnet run."));
    }

    [Fact]
    public void Takes_The_Last_Marker()
    {
        const string output = """
            ## Kết quả cổng biên dịch

            BUILD: FAIL

            (vòng trước)

            BUILD: PASS
            """;

        Assert.Equal(BuildVerdict.Pass, BuildVerdictParser.Parse(output));
    }

    [Fact]
    public void Tolerates_Bold_And_Case()
    {
        Assert.Equal(BuildVerdict.Fail, BuildVerdictParser.Parse("**BUILD**: **fail**"));
        Assert.Equal(BuildVerdict.Pass, BuildVerdictParser.Parse("  build = PASS"));
    }

    // Chở tóm tắt bàn giao của bước Implementation qua một vòng sửa lỗi biên dịch thì phải BỎ khối báo
    // cáo cũ dính trong đó — một "BUILD: FAIL" lỗi thời nằm cạnh "BUILD: PASS" mới chỉ làm người đọc
    // (Tech Lead ở bước review) hoang mang.
    [Fact]
    public void StripReport_Keeps_The_Agent_Handoff_And_Drops_The_Gate_Block()
    {
        var output = $"""
            Stack: ASP.NET Core. Chạy bằng `dotnet run`.

            {BuildVerdictParser.ReportHeading}

            BUILD: FAIL

            error CS0103
            """;

        var stripped = BuildVerdictParser.StripReport(output);

        Assert.Equal("Stack: ASP.NET Core. Chạy bằng `dotnet run`.", stripped);
        Assert.Equal(BuildVerdict.Unknown, BuildVerdictParser.Parse(stripped));
    }

    [Fact]
    public void StripReport_Leaves_An_Output_Without_A_Report_Untouched()
    {
        Assert.Equal("Đã sinh 12 file.", BuildVerdictParser.StripReport("Đã sinh 12 file.\n"));
        Assert.Equal(string.Empty, BuildVerdictParser.StripReport(null));
    }

    // Chỉ nhận dòng BẮT ĐẦU bằng marker: một câu văn kể chuyện ("nếu build: fail thì…") trong phần tóm
    // tắt của agent không được phép lật kết luận của cổng.
    [Fact]
    public void Ignores_A_Marker_Buried_Mid_Sentence()
    {
        Assert.Equal(BuildVerdict.Unknown, BuildVerdictParser.Parse("Tôi đã kiểm tra và thấy build: fail ở vòng đầu."));
    }
}

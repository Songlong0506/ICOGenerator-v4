using ICOGenerator.Services.Builds;
using Xunit;

namespace ICOGenerator.Tests.Builds;

/// <summary>
/// Output của một lệnh build hỏng có thể tới 100.000 ký tự, mà 95% là dòng tiến độ và đường dẫn. Nhồi
/// cả khối vào prompt sửa lỗi thì chính mấy dòng lỗi bị đẩy ra khỏi phần model đọc kỹ nhất — và vẫn
/// phải trả tiền token cho phần vô nghĩa.
/// </summary>
public class BuildOutputDigestTests
{
    [Fact]
    public void Keeps_Error_Lines_And_Drops_Noise()
    {
        var output = string.Join("\n",
            Enumerable.Repeat("  Determining projects to restore...", 40)
                .Append("/src/Api/OrderService.cs(42,13): error CS0103: The name 'repo' does not exist")
                .Append("  Restore complete")
                .Append("/src/Api/Startup.cs(10,5): error CS1061: no definition for 'AddOrders'"));

        var digest = BuildOutputDigest.Extract(output);

        Assert.Contains("error CS0103", digest);
        Assert.Contains("error CS1061", digest);
        Assert.DoesNotContain("Determining projects", digest);
    }

    [Fact]
    public void Recognises_Angular_And_Npm_Errors()
    {
        Assert.Contains("error TS2307", BuildOutputDigest.Extract("x\nsrc/app.ts:3:1 - error TS2307: Cannot find module\ny"));
        Assert.Contains("npm ERR!", BuildOutputDigest.Extract("npm ERR! code ELIFECYCLE"));
        Assert.Contains("ERROR in", BuildOutputDigest.Extract("ERROR in ./src/main.ts"));
    }

    // Không nhận ra dòng lỗi nào thì lấy PHẦN ĐUÔI: công cụ build nào cũng in lý do dừng ở cuối, còn
    // một digest rỗng thì task sửa lỗi không còn gì để bám.
    [Fact]
    public void Falls_Back_To_The_Tail_When_No_Error_Line_Is_Recognised()
    {
        var output = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"dòng {i}"));

        var digest = BuildOutputDigest.Extract(output);

        Assert.Contains("dòng 200", digest);
        Assert.DoesNotContain("dòng 1\n", digest);
    }

    [Fact]
    public void Handles_Empty_Output()
    {
        Assert.Equal("(không có output)", BuildOutputDigest.Extract(null));
        Assert.Equal("(không có output)", BuildOutputDigest.Extract("   "));
    }

    [Fact]
    public void Caps_Total_Size()
    {
        var giant = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"file{i}.cs(1,1): error CS0001: {new string('x', 300)}"));

        Assert.True(BuildOutputDigest.Extract(giant).Length <= 8200);
    }
}

using ICOGenerator.Services.Builds;
using Xunit;

namespace ICOGenerator.Tests.Builds;

/// <summary>
/// Agent có thể sinh ra một <c>package.json</c> sai cú pháp. Khi đó cổng biên dịch phải BỎ QUA phần
/// Node, chứ không được làm gãy cả lượt chấm bằng một JsonException ném từ giữa vòng lặp của worker.
/// </summary>
public class NodeScriptsTests
{
    [Fact]
    public void Detects_A_Real_Build_Script() =>
        Assert.True(NodeScripts.HasBuildScript("""{ "name": "app", "scripts": { "build": "ng build" } }"""));

    [Theory]
    [InlineData("""{ "scripts": { "start": "node ." } }""")]   // có scripts nhưng không có build
    [InlineData("""{ "scripts": { "build": "" } }""")]         // khai build rỗng
    [InlineData("""{ "scripts": { "build": ["ng"] } }""")]     // build không phải chuỗi
    [InlineData("""{ "name": "app" }""")]                       // không có scripts
    [InlineData("[]")]                                          // JSON hợp lệ nhưng không phải object
    [InlineData("{ khong phai json }")]                         // file hỏng
    [InlineData("")]
    [InlineData(null)]
    public void Returns_False_For_Everything_Else(string? content) =>
        Assert.False(NodeScripts.HasBuildScript(content));
}

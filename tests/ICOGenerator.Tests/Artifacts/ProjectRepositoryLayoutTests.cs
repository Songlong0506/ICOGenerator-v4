using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// ProjectRepositoryLayout là NGUỒN CHÂN LÝ chung của ba chỗ phải nhất trí với nhau: seeder dựng repo,
// cổng duyệt đòi Git URL trước bước Pull Request, và khối prompt bảo agent bàn giao repo nào. Ba chỗ đó
// nằm ở ba tầng khác nhau nên không có gì bắt chúng khớp nhau ngoài test này.
public class ProjectRepositoryLayoutTests
{
    // Khung Bosch = backend .NET và frontend Angular là HAI repo thật ⇒ hai thư mục, hai remote,
    // và (ở bước cuối) hai Pull Request.
    [Fact]
    public void Resolve_BoschTemplate_GivesTwoSlots_MappedToTheirOwnRemotes()
    {
        var slots = ProjectRepositoryLayout.Resolve(
            useBoschTemplate: true,
            backendGitUrl: "https://git.example.com/be.git",
            frontendGitUrl: "https://git.example.com/fe.git");

        Assert.Equal(2, slots.Count);
        Assert.Equal("04_Implementation/src/backend", slots[0].RelativePath);
        Assert.Equal("https://git.example.com/be.git", slots[0].RemoteUrl);
        Assert.Equal("04_Implementation/src/frontend", slots[1].RelativePath);
        Assert.Equal("https://git.example.com/fe.git", slots[1].RemoteUrl);
    }

    // Không dùng khung Bosch: agent sinh toàn bộ code vào MỘT cây 04_Implementation/src
    // (Prompts/Developer/implementation.v1.md) ⇒ một repo đích duy nhất, lấy Backend Git.
    [Fact]
    public void Resolve_WithoutBoschTemplate_GivesSingleSourceSlot()
    {
        var slots = ProjectRepositoryLayout.Resolve(
            useBoschTemplate: false,
            backendGitUrl: "https://git.example.com/app.git",
            frontendGitUrl: "https://git.example.com/ignored.git");

        var slot = Assert.Single(slots);
        Assert.Equal("04_Implementation/src", slot.RelativePath);
        Assert.Equal("https://git.example.com/app.git", slot.RemoteUrl);
    }

    // Ô nhập ở Agent Dashboard cho phép để trống: chuỗi trắng phải được coi như CHƯA cấu hình, nếu không
    // seeder sẽ gắn một remote rỗng và bước push fail với lý do khó hiểu.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_TreatsBlankUrlAsNotConfigured(string? url)
    {
        var slots = ProjectRepositoryLayout.Resolve(useBoschTemplate: false, backendGitUrl: url, frontendGitUrl: null);

        Assert.Null(Assert.Single(slots).RemoteUrl);
        Assert.Single(ProjectRepositoryLayout.MissingRemotes(slots));
    }

    // Cổng duyệt trước bước Pull Request đọc đúng danh sách này: thiếu URL của BẤT KỲ repo nào cũng chặn.
    [Fact]
    public void MissingRemotes_ListsOnlySlotsWithoutUrl()
    {
        var slots = ProjectRepositoryLayout.Resolve(
            useBoschTemplate: true, backendGitUrl: "https://git.example.com/be.git", frontendGitUrl: null);

        var missing = Assert.Single(ProjectRepositoryLayout.MissingRemotes(slots));
        Assert.Equal("frontend", missing.Label);
    }

    // Khối prompt phải in ĐÚNG chuỗi đường dẫn mà agent sẽ chép vào tham số repoPath của các tool git —
    // đây là chỗ duy nhất agent biết đường dẫn đó, sai một ký tự là tool trả "not a git repository root".
    [Fact]
    public void BuildPromptBlock_ListsEveryRepoPathAndRemote()
    {
        var block = ProjectRepositoryLayout.BuildPromptBlock(ProjectRepositoryLayout.Resolve(
            useBoschTemplate: true,
            backendGitUrl: "https://git.example.com/be.git",
            frontendGitUrl: "https://git.example.com/fe.git"));

        Assert.Contains("`04_Implementation/src/backend`", block);
        Assert.Contains("`04_Implementation/src/frontend`", block);
        Assert.Contains("https://git.example.com/be.git", block);
        Assert.Contains("https://git.example.com/fe.git", block);
    }

    // Repo chưa có remote vẫn được liệt kê (agent vẫn phải commit) nhưng phải nói rõ là push sẽ fail,
    // để agent báo đúng lý do ở câu trả lời cuối thay vì kết luận "đã bàn giao xong".
    [Fact]
    public void BuildPromptBlock_MarksRepoWithoutRemote()
    {
        var block = ProjectRepositoryLayout.BuildPromptBlock(ProjectRepositoryLayout.Resolve(
            useBoschTemplate: false, backendGitUrl: null, frontendGitUrl: null));

        Assert.Contains("CHƯA cấu hình remote", block);
    }

    // Không có repo nào ⇒ khối rỗng, prompt giữ nguyên như trước (cùng luật với khối UAT/quy ước POC).
    [Fact]
    public void BuildPromptBlock_IsEmpty_WhenNoRepositories()
    {
        Assert.Equal(string.Empty, ProjectRepositoryLayout.BuildPromptBlock([]));
    }
}

using ICOGenerator.Services.Tools.PullRequests;
using Xunit;

namespace ICOGenerator.Tests.Tools;

public class PullRequestUrlBuilderTests
{
    [Fact]
    public void GitHub_Https_BuildsCompareUrlWithTitle()
    {
        var url = PullRequestUrlBuilder.Build("https://github.com/acme/shop.git", "main", "feature/cart", "Add cart");
        // Nhánh nằm trong PATH của compare → giữ literal dấu '/'; title trong query → encode.
        Assert.Equal("https://github.com/acme/shop/compare/main...feature/cart?expand=1&title=Add%20cart", url);
    }

    [Fact]
    public void GitHub_Ssh_ScpForm_IsParsed()
    {
        var url = PullRequestUrlBuilder.Build("git@github.com:acme/shop.git", "main", "feature/x", null);
        Assert.Equal("https://github.com/acme/shop/compare/main...feature/x?expand=1", url);
    }

    [Fact]
    public void GitHub_StripsUserInfoAndPort()
    {
        var url = PullRequestUrlBuilder.Build("https://user@github.com:443/acme/shop", "main", "dev", null);
        Assert.Equal("https://github.com/acme/shop/compare/main...dev?expand=1", url);
    }

    [Fact]
    public void GitLab_BuildsMergeRequestUrl_WithEncodedQuery()
    {
        var url = PullRequestUrlBuilder.Build("https://gitlab.com/acme/shop.git", "main", "feature/cart", "Add cart");
        Assert.Equal(
            "https://gitlab.com/acme/shop/-/merge_requests/new"
            + "?merge_request%5Bsource_branch%5D=feature%2Fcart&merge_request%5Btarget_branch%5D=main"
            + "&merge_request%5Btitle%5D=Add%20cart",
            url);
    }

    [Fact]
    public void Bitbucket_BuildsPullRequestUrl()
    {
        var url = PullRequestUrlBuilder.Build("https://bitbucket.org/acme/shop.git", "develop", "feature/cart", "ignored");
        Assert.Equal("https://bitbucket.org/acme/shop/pull-requests/new?source=feature%2Fcart&dest=develop", url);
    }

    [Fact]
    public void AzureDevOps_BuildsPullRequestCreateUrl_KeepingGitSegment()
    {
        var url = PullRequestUrlBuilder.Build("https://dev.azure.com/acme/proj/_git/repo", "main", "feature/cart", "t");
        Assert.Equal("https://dev.azure.com/acme/proj/_git/repo/pullrequestcreate?sourceRef=feature%2Fcart&targetRef=main", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/acme/shop.git")] // host không nhận diện được
    [InlineData("not-a-url")]
    public void UnknownOrEmpty_ReturnsNull(string? remoteUrl)
        => Assert.Null(PullRequestUrlBuilder.Build(remoteUrl, "main", "feature/x", "t"));
}

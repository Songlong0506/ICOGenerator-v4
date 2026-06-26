using ICOGenerator.Services.Tools.PullRequests;
using Xunit;

namespace ICOGenerator.Tests.Tools;

public class GitHubPullRequestPublisherTests
{
    [Theory]
    [InlineData("https://github.com/acme/shop.git", "acme", "shop")]
    [InlineData("https://github.com/acme/shop", "acme", "shop")]
    [InlineData("git@github.com:acme/shop.git", "acme", "shop")]
    [InlineData("https://user@github.com/acme/shop.git", "acme", "shop")]
    public void TryGetRepo_ParsesGitHubOwnerAndRepo(string remote, string expectedOwner, string expectedRepo)
    {
        Assert.True(GitHubPullRequestPublisher.TryGetRepo(remote, out var owner, out var repo));
        Assert.Equal(expectedOwner, owner);
        Assert.Equal(expectedRepo, repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://gitlab.com/acme/shop.git")]      // không phải github
    [InlineData("https://dev.azure.com/acme/proj/_git/r")] // azure
    [InlineData("https://github.com/acme")]                // thiếu repo
    public void TryGetRepo_ReturnsFalse_ForNonGitHubOrIncomplete(string? remote)
        => Assert.False(GitHubPullRequestPublisher.TryGetRepo(remote, out _, out _));

    [Fact]
    public void ExtractHtmlUrl_ReadsHtmlUrlFromGitHubJson()
    {
        var json = """{"id":1,"html_url":"https://github.com/acme/shop/pull/42","state":"open"}""";
        Assert.Equal("https://github.com/acme/shop/pull/42", GitHubPullRequestPublisher.ExtractHtmlUrl(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"message":"Validation Failed"}""")] // không có html_url
    public void ExtractHtmlUrl_ReturnsNull_WhenMissingOrInvalid(string? json)
        => Assert.Null(GitHubPullRequestPublisher.ExtractHtmlUrl(json));
}

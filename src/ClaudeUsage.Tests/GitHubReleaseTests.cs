using ClaudeUsage.App.Services;
using Xunit;

namespace ClaudeUsage.Tests;

public class GitHubReleaseTests
{
    private const string Repo = "yumebi/example";

    private static string Json(string tag, string url) =>
        $"{{\"tag_name\":\"{tag}\",\"html_url\":\"{url}\",\"name\":\"x\"}}";

    [Fact]
    public void Parse_ReturnsVersionWithoutV_AndUrl()
    {
        var info = GitHubRelease.Parse(Json("v1.4.7", "https://github.com/yumebi/example/releases/tag/v1.4.7"), Repo);
        Assert.NotNull(info);
        Assert.Equal("1.4.7", info!.Version);
        Assert.Equal("https://github.com/yumebi/example/releases/tag/v1.4.7", info.Url);
    }

    [Fact]
    public void Parse_DropsForeignUrl()
    {
        var info = GitHubRelease.Parse(Json("v1.0.0", "https://evil.example.com/yumebi/example/"), Repo);
        Assert.NotNull(info);
        Assert.Null(info!.Url);
    }

    [Theory]
    [InlineData("auto-25")]
    [InlineData("")]
    [InlineData("1.2.x")]
    public void Parse_ReturnsNull_WhenTagIsNotAVersion(string tag)
    {
        Assert.Null(GitHubRelease.Parse(Json(tag, "https://github.com/yumebi/example/releases"), Repo));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"tag_name\":123}")]
    public void Parse_ReturnsNull_OnMalformedResponse(string json)
    {
        Assert.Null(GitHubRelease.Parse(json, Repo));
    }

    [Theory]
    [InlineData("1.3.6", "1.3.5", true)]
    [InlineData("1.10.0", "1.9.0", true)]      // 文字列比較だと逆になる
    [InlineData("1.9.0", "1.10.0", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("v1.3.5", "1.3.5", false)]     // 同じ版は更新なし
    [InlineData("1.3", "1.3.0", false)]        // 桁数が違っても同じ版
    [InlineData("1.3.4", "1.3.5", false)]      // 手元のほうが新しい
    [InlineData("auto-25", "1.0.0", false)]    // 版番号として読めなければ更新なし
    [InlineData("1.0.1", "dev", false)]
    public void IsNewer_ComparesNumerically(string latest, string current, bool expected)
    {
        Assert.Equal(expected, GitHubRelease.IsNewer(latest, current));
    }

    [Fact]
    public void IsNewer_ReturnsFalse_ForNull()
    {
        Assert.False(GitHubRelease.IsNewer(null, "1.0.0"));
        Assert.False(GitHubRelease.IsNewer("1.0.1", null));
    }

    [Theory]
    [InlineData("https://github.com/yumebi/example/releases/tag/v1.0.0", true)]
    [InlineData("http://github.com/yumebi/example/releases", false)]
    [InlineData("https://github.com/yumebi/example.evil.com/", false)]
    [InlineData("https://github.com/yumebi/other/releases", false)]
    [InlineData("https://github.com@evil.com/yumebi/example/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void IsAllowedUrl_OnlyOwnRepository(string url, bool expected)
    {
        Assert.Equal(expected, GitHubRelease.IsAllowedUrl(url, Repo));
    }

    [Fact]
    public void ApiUrl_PointsAtTheReleasesApi_NotVersionJson()
    {
        var url = GitHubRelease.ApiUrl(Repo);
        Assert.Equal("https://api.github.com/repos/yumebi/example/releases/latest", url);
        Assert.DoesNotContain("version.json", url);
    }
}

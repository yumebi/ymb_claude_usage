using ClaudeUsage.App.Services;

namespace ClaudeUsage.Tests;

public class UpdateCheckServiceTests
{
    private static string Json(string tag, string url = "https://github.com/yumebi/ymb_claude_usage/releases/tag/v9.9.9") =>
        $"{{\"tag_name\":\"{tag}\",\"html_url\":\"{url}\"}}";

    [Fact]
    public void NewerRelease_IsUpdateAvailable_WithTheReleasePageUrl()
    {
        var r = UpdateCheckService.Evaluate(Json("v1.3.0", "https://github.com/yumebi/ymb_claude_usage/releases/tag/v1.3.0"), "v1.2.4");
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, r.Status);
        Assert.Equal("1.3.0", r.LatestVersion);
        Assert.Equal("https://github.com/yumebi/ymb_claude_usage/releases/tag/v1.3.0", r.ReleaseUrl);
    }

    [Fact]
    public void SameRelease_IsUpToDate()
    {
        var r = UpdateCheckService.Evaluate(Json("v1.2.4"), "v1.2.4");
        Assert.Equal(UpdateCheckStatus.UpToDate, r.Status);
    }

    [Fact]
    public void NewerLocalBuild_IsUpToDate()
    {
        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateCheckService.Evaluate(Json("v1.2.4"), "v1.3.0").Status);
    }

    [Fact]
    public void NumericComparison_1_10_IsNewerThan_1_9()
    {
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, UpdateCheckService.Evaluate(Json("v1.10.0"), "v1.9.0").Status);
    }

    [Fact]
    public void ForeignUrl_FallsBackToTheLatestReleasePage()
    {
        var r = UpdateCheckService.Evaluate(Json("v2.0.0", "https://evil.example.com/"), "v1.0.0");
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, r.Status);
        Assert.Equal("https://github.com/yumebi/ymb_claude_usage/releases/latest", r.ReleaseUrl);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"tag_name\":\"auto-25\"}")]
    public void UnusableResponse_IsFailed(string json)
    {
        Assert.Equal(UpdateCheckStatus.Failed, UpdateCheckService.Evaluate(json, "v1.0.0").Status);
    }
}

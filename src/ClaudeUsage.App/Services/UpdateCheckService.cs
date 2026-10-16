using System.Net.Http;

namespace ClaudeUsage.App.Services;

public enum UpdateCheckStatus
{
    /// <summary>最新のバージョン。</summary>
    UpToDate,

    /// <summary>新しいバージョンが公開されている。</summary>
    UpdateAvailable,

    /// <summary>確認できなかった(オフライン・API制限・形式不正など)。</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? LatestVersion, string? ReleaseUrl);

/// <summary>
/// GitHub の最新リリース(Releases API)を見て、新しいバージョンがあるかを確認する。
/// version.json ではなく公開済みのリリースを見るのは、リリースに失敗しても version.json だけ
/// 先に更新されて、存在しない版を案内してしまうのを避けるため。
/// </summary>
public static class UpdateCheckService
{
    public const string Repo = "yumebi/ymb_claude_usage";

    private const string FallbackUrl = "https://github.com/" + Repo + "/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        // GitHub API は User-Agent が必須。応答が無いまま固まらないよう、短めに打ち切る
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YmbClaudeUsage-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>API のレスポンスから、更新の有無を判定する(通信から切り離した純粋な処理)。</summary>
    public static UpdateCheckResult Evaluate(string json, string currentVersion)
    {
        var release = GitHubRelease.Parse(json, Repo);
        if (release is null)
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, null);

        return GitHubRelease.IsNewer(release.Version, currentVersion)
            ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release.Version, release.Url ?? FallbackUrl)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, release.Version, release.Url);
    }

    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion)
    {
        try
        {
            var json = await Http.GetStringAsync(GitHubRelease.ApiUrl(Repo));
            return Evaluate(json, currentVersion);
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, null);
        }
    }
}

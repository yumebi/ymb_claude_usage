using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsage.App.Services;

/// <summary>
/// GitHub Releases API(/releases/latest)のレスポンスの解釈と、更新の有無の判定。
///
/// 公開済みのリリースそのものを見る。version.json を見る方式だと、リリース(ビルド・公開)に失敗しても
/// version.json だけ先に更新されるため、存在しないリリースを「新しいバージョンがあります」と案内してしまう。
/// 通信やUIから切り離した純粋な処理にして、単体テストできるようにしてある。
/// </summary>
public static class GitHubRelease
{
    /// <summary>取り出した最新リリース。Url は自リポジトリのページだけ(それ以外は null)。</summary>
    public sealed record Info(string Version, string? Url);

    private static readonly Regex VersionPattern = new(@"^[vV]?(\d+(?:\.\d+){0,3})$", RegexOptions.Compiled);

    public static string ApiUrl(string repo) => $"https://api.github.com/repos/{repo}/releases/latest";

    /// <summary>
    /// レスポンスから版番号とリリースページのURLを取り出す。
    /// 版番号として読めない(タグが "auto-25" など)、または形式が不正なら null。
    /// </summary>
    public static Info? Parse(string json, string repo)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tag)
                || tag.ValueKind != JsonValueKind.String)
                return null;

            var parts = ParseVersion(tag.GetString());
            if (parts is null) return null;

            string? url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            return new Info(string.Join('.', parts), IsAllowedUrl(url, repo) ? url : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.2.3" / "1.2" などを数値の配列にする。版番号として読めなければ null。</summary>
    public static int[]? ParseVersion(string? text)
    {
        if (text is null) return null;
        var m = VersionPattern.Match(text.Trim());
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Split('.');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out result[i])) return null;
        }
        return result;
    }

    /// <summary>
    /// latest が current より新しいか。数値で比較する("1.10.0" は "1.9.0" より新しい。桁数が違っても同じ版は同じ)。
    /// どちらかが版番号として読めない場合は false(誤って「更新あり」と案内しない)。
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
    {
        var a = ParseVersion(latest);
        var b = ParseVersion(current);
        if (a is null || b is null) return false;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var diff = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
            if (diff != 0) return diff > 0;
        }
        return false;
    }

    /// <summary>案内するリンクは自リポジトリの https://github.com/&lt;repo&gt;/ 配下だけ。</summary>
    public static bool IsAllowedUrl(string? url, string repo) =>
        url is not null && url.StartsWith($"https://github.com/{repo}/", StringComparison.Ordinal);
}

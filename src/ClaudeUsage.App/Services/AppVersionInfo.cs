using System.Reflection;

namespace ClaudeUsage.App.Services;

/// <summary>
/// 実行中アセンブリのバージョンを表示用文字列に整形する。
/// csproj の &lt;Version&gt; は開発時の既定値でしかなく、実際のリリースバージョンは
/// CI(release.yml)が publish 時に -p:Version= で上書きするため、
/// 常に一致させるためアセンブリバージョンから動的に取得する。
/// </summary>
public static class AppVersionInfo
{
    /// <summary>"v1.0.3" のような表示用バージョン文字列。Revisionが0なら省略する。</summary>
    public static string Display { get; } = BuildDisplay();

    private static string BuildDisplay()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null)
            return "v?";

        return version.Revision > 0
            ? $"v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}

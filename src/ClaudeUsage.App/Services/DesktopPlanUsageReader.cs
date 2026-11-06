using System.IO;
using System.Text.Json;

namespace ClaudeUsage.App.Services;

/// <summary>デスクトップアプリが記録した、ある時点のプラン使用率(%)。</summary>
/// <param name="SampledAt">採取時刻。</param>
/// <param name="FiveHourPercent">5時間枠の使用率。</param>
/// <param name="WeeklyPercent">週間(全モデル)の使用率。</param>
public sealed record DesktopPlanUsage(DateTimeOffset SampledAt, double FiveHourPercent, double WeeklyPercent);

/// <summary>
/// Claudeデスクトップアプリが %APPDATA%\Claude\plan-usage-history.json に残している
/// 使用率の履歴から、最新の1サンプルを読む。
///
/// これは OAuth使用量API が使えない時(~/.claude/.credentials.json の失効など)の
/// フォールバック専用。認証情報には一切触れず、デスクトップアプリ自身が平文で
/// 書いたファイルを読むだけ。
///
/// 注意: 非公開の内部形式(version 2)で、将来変わりうる。形式が想定と違えば何も返さない。
/// 採取間隔は不規則で、リセット時刻やモデル別の内訳は持たない。
/// </summary>
public static class DesktopPlanUsageReader
{
    private const int SupportedVersion = 2;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "plan-usage-history.json");

    /// <summary>最新サンプルを返す。ファイルが無い・形式が違う・読めない場合は null。</summary>
    public static DesktopPlanUsage? ReadLatest()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            // デスクトップアプリが書き込み中でも読めるよう共有を許可する
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number ||
                ver.GetInt32() != SupportedVersion)
                return null;
            if (!root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
                return null;

            long bestT = long.MinValue;
            double bestFh = 0, bestSd = 0;
            foreach (var s in samples.EnumerateArray())
            {
                if (!s.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.Number ||
                    !s.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object ||
                    !u.TryGetProperty("fh", out var fh) || fh.ValueKind != JsonValueKind.Number ||
                    !u.TryGetProperty("sd", out var sd) || sd.ValueKind != JsonValueKind.Number)
                    continue;

                var t = tEl.GetInt64();
                if (t > bestT)
                {
                    bestT = t;
                    bestFh = fh.GetDouble();
                    bestSd = sd.GetDouble();
                }
            }

            if (bestT == long.MinValue)
                return null;

            return new DesktopPlanUsage(DateTimeOffset.FromUnixTimeMilliseconds(bestT), bestFh, bestSd);
        }
        catch
        {
            // フォールバックなので、読めなければ黙って「無い」扱いにする
            return null;
        }
    }
}

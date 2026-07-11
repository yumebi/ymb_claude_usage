using System.IO;
using System.Text.Json;

namespace ClaudeUsage.App.Services;

/// <summary>モデル1つ分のトークン集計。</summary>
public sealed class ModelTotals
{
    public long Input;
    public long Output;
    public long CacheRead;
    public long CacheCreate;
    public int Requests;
}

/// <summary>今日/過去7日間のモデル別集計結果。</summary>
public sealed record LocalUsage(
    IReadOnlyDictionary<string, ModelTotals> Today,
    IReadOnlyDictionary<string, ModelTotals> Week);

/// <summary>
/// ~/.claude/projects/**/*.jsonl(Claude Codeのセッションログ)を走査して
/// モデル別トークン使用量を集計する。ネットワーク・認証不要。
/// ファイル単位で (更新時刻, サイズ) キーのキャッシュを持ち、再走査を最小化する。
/// キャッシュは生エントリの一覧ではなく「ローカル日付×モデル別の集計済みトークン数」のみを
/// 保持するため、セッションログが多いユーザーでも常駐メモリが肥大化しない。
/// 重複排除(message.id + requestId)はファイルの日別集計を作り直すタイミングで1回だけ行う
/// (ファイル単位のためファイルをまたいだ重複は排除されないが、実運用上は許容範囲)。
/// today/week の判定は日付(ローカル日)単位の粒度で行う。
/// </summary>
public sealed class LocalUsageScanner
{
    private sealed record FileCache(
        DateTime LastWriteUtc,
        long Length,
        Dictionary<DateOnly, Dictionary<string, ModelTotals>> DailyTotals);

    private readonly Dictionary<string, FileCache> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static string ProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public LocalUsage Scan()
    {
        var now = DateTimeOffset.Now;
        var weekStart = now.AddDays(-7);
        var weekStartDate = DateOnly.FromDateTime(weekStart.LocalDateTime);
        var todayDate = DateOnly.FromDateTime(now.LocalDateTime);

        var today = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);
        var week = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(ProjectsRoot))
            return new LocalUsage(today, week);

        foreach (var path in Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            FileInfo info;
            try { info = new FileInfo(path); } catch { continue; }

            // 集計期間より古いファイルはキャッシュごと捨てる
            if (info.LastWriteTimeUtc < weekStart.UtcDateTime)
            {
                _cache.Remove(path);
                continue;
            }

            if (!_cache.TryGetValue(path, out var cached) ||
                cached.LastWriteUtc != info.LastWriteTimeUtc || cached.Length != info.Length)
            {
                cached = new FileCache(info.LastWriteTimeUtc, info.Length, ParseFileDaily(path, weekStart));
                _cache[path] = cached;
            }

            foreach (var (date, models) in cached.DailyTotals)
            {
                if (date < weekStartDate)
                    continue;

                foreach (var (model, totals) in models)
                {
                    AddInto(week, model, totals);
                    if (date == todayDate)
                        AddInto(today, model, totals);
                }
            }
        }

        return new LocalUsage(today, week);
    }

    private static void AddInto(Dictionary<string, ModelTotals> map, string model, ModelTotals src)
    {
        if (!map.TryGetValue(model, out var t))
            map[model] = t = new ModelTotals();
        t.Input += src.Input;
        t.Output += src.Output;
        t.CacheRead += src.CacheRead;
        t.CacheCreate += src.CacheCreate;
        t.Requests += src.Requests;
    }

    /// <summary>
    /// ファイルを1行ずつパースし、ローカル日付×モデルごとの集計を作る。
    /// 生の Entry リストは保持せず、この場で集計値に畳み込む。
    /// 重複排除(message.id+requestId)はここで1回だけ行う。
    /// </summary>
    private static Dictionary<DateOnly, Dictionary<string, ModelTotals>> ParseFileDaily(
        string path, DateTimeOffset weekStart)
    {
        var result = new Dictionary<DateOnly, Dictionary<string, ModelTotals>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || !line.Contains("\"usage\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("timestamp", out var tsEl) ||
                        !DateTimeOffset.TryParse(tsEl.GetString(), out var ts) ||
                        ts < weekStart)
                        continue;

                    if (!root.TryGetProperty("message", out var msg) ||
                        msg.ValueKind != JsonValueKind.Object ||
                        !msg.TryGetProperty("model", out var modelEl) ||
                        !msg.TryGetProperty("usage", out var usage) ||
                        usage.ValueKind != JsonValueKind.Object)
                        continue;

                    var model = modelEl.GetString();
                    if (string.IsNullOrEmpty(model) || model == "<synthetic>")
                        continue;

                    var msgId = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var reqId = root.TryGetProperty("requestId", out var rqEl) ? rqEl.GetString() : null;
                    var dedup = msgId is null && reqId is null ? "" : $"{msgId}:{reqId}";
                    if (dedup.Length > 0 && !seen.Add(dedup))
                        continue;

                    var date = DateOnly.FromDateTime(ts.LocalDateTime);
                    if (!result.TryGetValue(date, out var models))
                        result[date] = models = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);

                    if (!models.TryGetValue(model, out var t))
                        models[model] = t = new ModelTotals();

                    t.Input += GetLong(usage, "input_tokens");
                    t.Output += GetLong(usage, "output_tokens");
                    t.CacheRead += GetLong(usage, "cache_read_input_tokens");
                    t.CacheCreate += GetLong(usage, "cache_creation_input_tokens");
                    t.Requests++;
                }
                catch (JsonException)
                {
                    // 書き込み途中の行などは無視
                }
            }
        }
        catch
        {
            // ロック中・削除済みファイルは無視
        }
        return result;
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : 0;
}

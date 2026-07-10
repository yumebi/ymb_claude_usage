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
/// </summary>
public sealed class LocalUsageScanner
{
    private sealed record Entry(DateTimeOffset Timestamp, string Model, string DedupKey,
        long Input, long Output, long CacheRead, long CacheCreate);

    private sealed record FileCache(DateTime LastWriteUtc, long Length, List<Entry> Entries);

    private readonly Dictionary<string, FileCache> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static string ProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public LocalUsage Scan()
    {
        var now = DateTimeOffset.Now;
        var weekStart = now.AddDays(-7);
        var todayStart = new DateTimeOffset(now.Date, now.Offset);

        var today = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);
        var week = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);

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
                cached = new FileCache(info.LastWriteTimeUtc, info.Length, ParseFile(path, weekStart));
                _cache[path] = cached;
            }

            foreach (var e in cached.Entries)
            {
                if (e.Timestamp < weekStart)
                    continue;
                // 同一応答が複数行/複数ファイルに現れることがあるため message.id + requestId で重複排除
                if (e.DedupKey.Length > 0 && !seen.Add(e.DedupKey))
                    continue;

                Add(week, e);
                if (e.Timestamp >= todayStart)
                    Add(today, e);
            }
        }

        return new LocalUsage(today, week);
    }

    private static void Add(Dictionary<string, ModelTotals> map, Entry e)
    {
        if (!map.TryGetValue(e.Model, out var t))
            map[e.Model] = t = new ModelTotals();
        t.Input += e.Input;
        t.Output += e.Output;
        t.CacheRead += e.CacheRead;
        t.CacheCreate += e.CacheCreate;
        t.Requests++;
    }

    private static List<Entry> ParseFile(string path, DateTimeOffset weekStart)
    {
        var entries = new List<Entry>();
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

                    entries.Add(new Entry(ts, model, dedup,
                        GetLong(usage, "input_tokens"),
                        GetLong(usage, "output_tokens"),
                        GetLong(usage, "cache_read_input_tokens"),
                        GetLong(usage, "cache_creation_input_tokens")));
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
        return entries;
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : 0;
}

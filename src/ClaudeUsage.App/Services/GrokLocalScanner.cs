using System.IO;
using System.Text.Json;

namespace ClaudeUsage.App.Services;

/// <summary>今日/過去7日間のモデル別集計結果 + 直近の画像生成残数(Grok版)。</summary>
/// <param name="BalanceExhaustedAt">
/// 現在 Grok Build の利用残高が切れている場合、その402エラーが発生した日時(UTC)。
/// 切れていない(=それ以降に推論成功が記録されている、または402記録が無い)場合は null。
/// </param>
public sealed record GrokUsage(
    IReadOnlyDictionary<string, ModelTotals> Today,
    IReadOnlyDictionary<string, ModelTotals> Week,
    long? ImagesRemaining,
    DateTimeOffset? BalanceExhaustedAt);

/// <summary>
/// ~/.grok/logs/unified.jsonl(Grok Build CLIの統合ログ)を走査して
/// モデル別トークン使用量を集計する。ネットワーク・認証不要(auth.jsonは一切読まない)。
///
/// unified.jsonl は Claude Code の ~/.claude/projects/**/*.jsonl と違い単一ファイルなので、
/// LocalUsageScanner と同じ「ファイル単位で (更新時刻, サイズ) キーのキャッシュを持ち、
/// 保持する値は生エントリではなく日付×モデル別の集計済みトークン数のみ」という設計を
/// そのままこの1ファイルに適用する。
///
/// レコード構造:
/// - "msg":"model changed" な行は ctx.model にモデル名、sid にセッションIDを持つ
///   (トークン使用量そのものは持たない)。
/// - ctx.completion_tokens を持つ行(msg = shell.turn.inference_done)がトークン使用量本体。
///   ctx.prompt_tokens(入力)/ctx.completion_tokens(出力)/ctx.cached_prompt_tokens(キャッシュ読取)/
///   ctx.reasoning_tokens(推論)を持つが、モデル名はこのレコード自体には無いので sid 経由で解決する。
/// - "msg":"shell.image_budget" な行の ctx.images_remaining が画像生成の残数(最新値を使う)。
///
/// モデル名解決: まずファイル全体から sid→model のマップを "model changed" 行だけで作る
/// (1セッション内で先に出るため、ファイル全体を軽く読むだけで足りる)。
/// これで解決できない sid は ~/.grok/sessions/*/&lt;sid&gt;/chat_history.jsonl の model_id を見に行く
/// (フォールバック。単一ファイルではなくディレクトリ探索が要るため、解決できない sid がある時だけ行う)。
/// それでも解決できなければ "Grok" にフォールバックする。
/// </summary>
public sealed class GrokLocalScanner
{
    private sealed record FileCache(
        DateTime LastWriteUtc,
        long Length,
        Dictionary<DateOnly, Dictionary<string, ModelTotals>> DailyTotals,
        long? ImagesRemaining,
        DateTimeOffset? LastSuccessTs,
        DateTimeOffset? LastExhaustedTs);

    private FileCache? _cache;

    private static string GrokRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");

    private static string UnifiedLogPath => Path.Combine(GrokRoot, "logs", "unified.jsonl");

    private static string SessionsRoot => Path.Combine(GrokRoot, "sessions");

    /// <summary>~/.grok/logs/unified.jsonl が存在するか(Grokを使っているかの判定に使う)。</summary>
    public static bool IsAvailable => File.Exists(UnifiedLogPath);

    public GrokUsage Scan()
    {
        var now = DateTimeOffset.Now;
        var weekStart = now.AddDays(-7);
        var weekStartDate = DateOnly.FromDateTime(weekStart.LocalDateTime);
        var todayDate = DateOnly.FromDateTime(now.LocalDateTime);

        var today = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);
        var week = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);

        var path = UnifiedLogPath;
        if (!File.Exists(path))
            return new GrokUsage(today, week, null, null);

        FileInfo info;
        try { info = new FileInfo(path); } catch { return new GrokUsage(today, week, null, null); }

        if (_cache is null || _cache.LastWriteUtc != info.LastWriteTimeUtc || _cache.Length != info.Length)
        {
            var parsed = ParseDaily(path, weekStart);
            _cache = new FileCache(
                info.LastWriteTimeUtc, info.Length, parsed.Daily, ParseImagesRemaining(path),
                parsed.LastSuccessTs, parsed.LastExhaustedTs);
        }

        foreach (var (date, models) in _cache.DailyTotals)
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

        // 残高切れ(402)が最後の推論成功より新しければ「現在残高切れ」と判定する。
        // 次に成功すればそちらのtsの方が新しくなるので自動的に false に戻る。
        DateTimeOffset? balanceExhaustedAt =
            _cache.LastExhaustedTs is { } exhaustedTs &&
            (_cache.LastSuccessTs is null || exhaustedTs > _cache.LastSuccessTs)
                ? exhaustedTs
                : null;

        return new GrokUsage(today, week, _cache.ImagesRemaining, balanceExhaustedAt);
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

    private sealed record TokenRecord(
        DateOnly Date, string Sid, long Prompt, long Completion, long CachedPrompt, long Reasoning);

    private sealed record ParseResult(
        Dictionary<DateOnly, Dictionary<string, ModelTotals>> Daily,
        DateTimeOffset? LastSuccessTs,
        DateTimeOffset? LastExhaustedTs);

    /// <summary>
    /// unified.jsonl を1回読み、(1) sid→model マップ、(2) トークン使用量レコード一覧、
    /// (3) 最後の推論成功時刻、(4) 最後の残高切れ(402)時刻を作った上で
    /// 日付×モデル別の集計済み値に畳み込む。生の Entry リストは戻り値には残さない。
    /// (3)(4)はファイル全体(週次フィルタの対象外)から求める。ファイルは1回しか読まない。
    /// </summary>
    private static ParseResult ParseDaily(string path, DateTimeOffset weekStart)
    {
        var sidToModel = new Dictionary<string, string>(StringComparer.Ordinal);
        var records = new List<TokenRecord>();
        DateTimeOffset? lastSuccessTs = null;
        DateTimeOffset? lastExhaustedTs = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                    continue;

                // "model changed" 行: sid→model を記録
                if (line.Contains("\"model changed\"", StringComparison.Ordinal))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("sid", out var sidEl) &&
                            root.TryGetProperty("ctx", out var ctxEl) &&
                            ctxEl.TryGetProperty("model", out var modelEl))
                        {
                            var sid = sidEl.GetString();
                            var model = modelEl.GetString();
                            if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(model))
                                sidToModel[sid] = model;
                        }
                    }
                    catch (JsonException) { }
                    continue;
                }

                // 残高切れ(402)行: msg は shell.turn.inference_failed / turn.terminal_failure の
                // 両方があり得るため msg では絞らず、status_code と message で判定する
                if (line.Contains("usage balance exhausted", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("ts", out var tsEl) &&
                            DateTimeOffset.TryParse(tsEl.GetString(), out var ts) &&
                            root.TryGetProperty("ctx", out var ctx) &&
                            ctx.ValueKind == JsonValueKind.Object &&
                            ctx.TryGetProperty("status_code", out var codeEl) &&
                            codeEl.ValueKind == JsonValueKind.Number &&
                            codeEl.GetInt32() == 402 &&
                            ctx.TryGetProperty("message", out var msgEl) &&
                            (msgEl.GetString() ?? "").Contains("usage balance exhausted", StringComparison.OrdinalIgnoreCase))
                        {
                            if (lastExhaustedTs is null || ts > lastExhaustedTs)
                                lastExhaustedTs = ts;
                        }
                    }
                    catch (JsonException) { }
                    continue;
                }

                if (!line.Contains("\"completion_tokens\"", StringComparison.Ordinal))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("ts", out var tsEl) ||
                        !DateTimeOffset.TryParse(tsEl.GetString(), out var ts))
                        continue;

                    if (lastSuccessTs is null || ts > lastSuccessTs)
                        lastSuccessTs = ts;

                    if (ts < weekStart)
                        continue;

                    if (!root.TryGetProperty("ctx", out var ctx) || ctx.ValueKind != JsonValueKind.Object)
                        continue;

                    var sid = root.TryGetProperty("sid", out var sidEl2) ? sidEl2.GetString() : null;
                    if (string.IsNullOrEmpty(sid))
                        continue;

                    var date = DateOnly.FromDateTime(ts.LocalDateTime);
                    records.Add(new TokenRecord(
                        date, sid,
                        GetLong(ctx, "prompt_tokens"),
                        GetLong(ctx, "completion_tokens"),
                        GetLong(ctx, "cached_prompt_tokens"),
                        GetLong(ctx, "reasoning_tokens")));
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
            return new ParseResult(new Dictionary<DateOnly, Dictionary<string, ModelTotals>>(), null, null);
        }

        // ファイル内で解決できなかった sid は sessions/*/<sid>/chat_history.jsonl にフォールバック
        var unresolvedSids = records.Select(r => r.Sid).Distinct(StringComparer.Ordinal)
            .Where(sid => !sidToModel.ContainsKey(sid))
            .ToList();
        foreach (var sid in unresolvedSids)
        {
            var model = ResolveModelFromSessions(sid);
            if (model is not null)
                sidToModel[sid] = model;
        }

        var result = new Dictionary<DateOnly, Dictionary<string, ModelTotals>>();
        foreach (var r in records)
        {
            var model = sidToModel.TryGetValue(r.Sid, out var m) ? m : "Grok";

            if (!result.TryGetValue(r.Date, out var models))
                result[r.Date] = models = new Dictionary<string, ModelTotals>(StringComparer.OrdinalIgnoreCase);

            if (!models.TryGetValue(model, out var t))
                models[model] = t = new ModelTotals();

            t.Input += r.Prompt;
            // reasoning_tokens は独立フィールドとして提供されるが、UI側の「今日/7日間」合計は
            // Input+Output でしか出さないため、出力側トークン消費として Output に合算する
            // (ModelTotals は Claude 側と共有の型なのでフィールドは増やさない)。
            t.Output += r.Completion + r.Reasoning;
            t.CacheRead += r.CachedPrompt;
            t.Requests++;
        }
        return new ParseResult(result, lastSuccessTs, lastExhaustedTs);
    }

    /// <summary>
    /// ~/.grok/sessions/&lt;エンコード済み作業ディレクトリ&gt;/&lt;sid&gt;/chat_history.jsonl を探し、
    /// 見つかれば model_id を返す。見つからなければ null。
    /// </summary>
    private static string? ResolveModelFromSessions(string sid)
    {
        if (!Directory.Exists(SessionsRoot))
            return null;

        try
        {
            foreach (var workDir in Directory.EnumerateDirectories(SessionsRoot))
            {
                var chatPath = Path.Combine(workDir, sid, "chat_history.jsonl");
                if (!File.Exists(chatPath))
                    continue;

                try
                {
                    using var stream = new FileStream(chatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        if (!line.Contains("\"model_id\"", StringComparison.Ordinal))
                            continue;
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("model_id", out var idEl))
                        {
                            var id = idEl.GetString();
                            if (!string.IsNullOrEmpty(id))
                                return id;
                        }
                    }
                }
                catch
                {
                    // 読めなければ諦める
                }
                return null;
            }
        }
        catch
        {
            // ディレクトリ列挙に失敗した場合は諦める
        }
        return null;
    }

    /// <summary>ファイル内で最も新しい ts を持つ "images_remaining" の値を返す。無ければ null。</summary>
    private static long? ParseImagesRemaining(string path)
    {
        long? best = null;
        DateTimeOffset bestTs = DateTimeOffset.MinValue;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || !line.Contains("\"images_remaining\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("ts", out var tsEl) ||
                        !DateTimeOffset.TryParse(tsEl.GetString(), out var ts))
                        continue;
                    if (!root.TryGetProperty("ctx", out var ctx) ||
                        !ctx.TryGetProperty("images_remaining", out var irEl) ||
                        irEl.ValueKind != JsonValueKind.Number)
                        continue;

                    if (best is null || ts >= bestTs)
                    {
                        best = irEl.GetInt64();
                        bestTs = ts;
                    }
                }
                catch (JsonException)
                {
                    // 書き込み途中の行などは無視
                }
            }
        }
        catch
        {
            return null;
        }
        return best;
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : 0;
}

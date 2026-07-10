using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ClaudeUsage.App.Models;

namespace ClaudeUsage.App.Services.Providers;

/// <summary>providers.json の1プロバイダー分の設定。</summary>
public sealed class RestProviderConfig
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string Method { get; set; } = "GET";
    public Dictionary<string, string> Headers { get; set; } = [];
    public int RefreshMinutes { get; set; } = 15;
    public List<GaugeConfig> Gauges { get; set; } = [];
    public List<RowConfig> Rows { get; set; } = [];
}

/// <summary>%バー1本分の設定。MaxPath / MaxValue のどちらも無ければ値表示のみ。</summary>
public sealed class GaugeConfig
{
    public string? Label { get; set; }
    public string? ValuePath { get; set; }
    public string? MaxPath { get; set; }
    public double? MaxValue { get; set; }
}

/// <summary>テーブル1行分の設定。</summary>
public sealed class RowConfig
{
    public string? Label { get; set; }
    public string? Path { get; set; }
}

/// <summary>
/// %APPDATA%\YmbClaudeUsage\providers.json で定義された任意のRESTエンドポイントから
/// 使用量を取得する汎用プロバイダー。JSONレスポンスからドット区切りパス
/// (例: "data.credits.remaining"、配列は数字で "items.0.count")で値を抜き出す。
/// 取得失敗はパネル内のエラー表示に留め、他プロバイダーへ影響させない。
/// </summary>
public sealed class GenericRestProvider : IUsageProvider
{
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly HttpClient _http;
    private readonly RestProviderConfig _cfg;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private ProviderPanelData? _cache;

    public string Name { get; }

    public GenericRestProvider(HttpClient http, RestProviderConfig cfg)
    {
        _http = http;
        _cfg = cfg;
        Name = cfg.Name ?? "?";
    }

    /// <summary>providers.json を読み、プロバイダー一覧を返す。ファイルなしなら空。</summary>
    public static List<IUsageProvider> LoadAll(HttpClient http)
    {
        var path = Path.Combine(AppSettings.Directory, "providers.json");
        if (!File.Exists(path))
            return [];
        try
        {
            var configs = JsonSerializer.Deserialize<List<RestProviderConfig>>(File.ReadAllText(path), ConfigJson) ?? [];
            var list = new List<IUsageProvider>();
            foreach (var cfg in configs)
            {
                if (string.IsNullOrWhiteSpace(cfg?.Name) || string.IsNullOrWhiteSpace(cfg.Url))
                    list.Add(new StaticErrorProvider(cfg?.Name ?? "(名前なし)", "providers.json: name と url は必須です"));
                else
                    list.Add(new GenericRestProvider(http, cfg));
            }
            return list;
        }
        catch (Exception ex)
        {
            return [new StaticErrorProvider("providers.json", $"providers.json 読込失敗: {ex.Message}")];
        }
    }

    public async Task<ProviderPanelData> FetchAsync(bool force, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _cfg.RefreshMinutes));
        if (!force && _cache is not null && DateTimeOffset.Now - _lastFetch < interval)
            return _cache;

        ProviderPanelData data;
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(_cfg.Method), _cfg.Url);
            foreach (var (key, value) in _cfg.Headers)
                req.Headers.TryAddWithoutValidation(key, value);

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            data = res.IsSuccessStatusCode
                ? Parse(body)
                : ProviderPanelData.FromError($"{Name}: HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            data = ProviderPanelData.FromError($"{Name}: 取得失敗 ({ex.Message})");
        }

        _lastFetch = DateTimeOffset.Now;
        _cache = data;
        return data;
    }

    private ProviderPanelData Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var gauges = new List<GaugeRow>();
        foreach (var g in _cfg.Gauges)
        {
            if (string.IsNullOrWhiteSpace(g.ValuePath))
                continue;
            var valueEl = ResolvePath(root, g.ValuePath);
            var value = AsNumber(valueEl);
            var label = g.Label ?? g.ValuePath;
            if (value is null)
            {
                gauges.Add(new GaugeRow(label, "取得不可", null, null));
                continue;
            }

            double? max = g.MaxValue ??
                (g.MaxPath is not null ? AsNumber(ResolvePath(root, g.MaxPath)) : null);
            if (max is > 0)
            {
                var pct = value.Value / max.Value * 100;
                gauges.Add(new GaugeRow(label, $"{FormatNumber(value.Value)} / {FormatNumber(max.Value)}", pct, null));
            }
            else
            {
                gauges.Add(new GaugeRow(label, FormatNumber(value.Value), null, null));
            }
        }

        var rows = new List<TableRow>();
        foreach (var r in _cfg.Rows)
        {
            if (string.IsNullOrWhiteSpace(r.Path))
                continue;
            var el = ResolvePath(root, r.Path);
            rows.Add(new TableRow(r.Label ?? r.Path, "", AsDisplay(el)));
        }

        return new ProviderPanelData(gauges, null, "", "", "", rows, null, null);
    }

    /// <summary>ドット区切りの簡易JSONパス解決。配列は数字セグメントでインデックス指定。</summary>
    private static JsonElement? ResolvePath(JsonElement root, string path)
    {
        var cur = root;
        foreach (var seg in path.Split('.'))
        {
            if (cur.ValueKind == JsonValueKind.Array && int.TryParse(seg, out var index))
            {
                if (index < 0 || index >= cur.GetArrayLength())
                    return null;
                cur = cur[index];
            }
            else if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(seg, out var next))
            {
                cur = next;
            }
            else
            {
                return null;
            }
        }
        return cur;
    }

    private static double? AsNumber(JsonElement? el) => el?.ValueKind switch
    {
        JsonValueKind.Number => el.Value.GetDouble(),
        JsonValueKind.String when double.TryParse(el.Value.GetString(), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static string AsDisplay(JsonElement? el) => el?.ValueKind switch
    {
        JsonValueKind.String => el.Value.GetString() ?? "",
        JsonValueKind.Number => FormatNumber(el.Value.GetDouble()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        null or JsonValueKind.Null or JsonValueKind.Undefined => "-",
        _ => el.Value.GetRawText(),
    };

    private static string FormatNumber(double v) =>
        v == Math.Floor(v) ? v.ToString("N0") : v.ToString("0.##");
}

/// <summary>設定不備などを常にエラーとして表示するだけのプロバイダー。</summary>
public sealed class StaticErrorProvider(string name, string error) : IUsageProvider
{
    public string Name => name;

    public Task<ProviderPanelData> FetchAsync(bool force, CancellationToken ct) =>
        Task.FromResult(ProviderPanelData.FromError(error));
}

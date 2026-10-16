namespace ClaudeUsage.App.Services;

/// <summary>
/// 「5/3(日) 14:00 リセット(あと2時間30分)」の表示文字列を作る。
/// 「あと◯分」の部分は時間とともに変わるので、現在時刻を引数で受けて、
/// 画面側が定期的に呼び直せるようにしてある(テストでも時刻を固定できる)。
/// </summary>
public static class ResetTextFormatter
{
    public static string Format(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
            return "";

        var local = resetsAt.Value.ToLocalTime();
        var remain = local - now;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;

        var remainText = remain.TotalDays >= 1
            ? $"あと{(int)remain.TotalDays}日{remain.Hours}時間"
            : remain.TotalHours >= 1
                ? $"あと{(int)remain.TotalHours}時間{remain.Minutes}分"
                : $"あと{remain.Minutes}分";
        return $"{local:M/d(ddd) HH:mm} リセット({remainText})";
    }
}

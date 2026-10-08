using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliWatch.Core;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(property); return true;
    }
}

public enum QueryState { Pending, Running, Success, Failed }
public sealed class Anchor : Observable
{
    public long Uid { get; set; }
    private string name = "";
    public string Name { get => name; set => Set(ref name, value); }
    public HashSet<string> Sources { get; set; } = [];
    public long? LastSeconds { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? SuccessRun { get; set; }
    public QueryState State { get; set; }
    public string Error { get; set; } = "";
    [JsonIgnore] public string SourceText => string.Join(" · ", Sources.Order());
    [JsonIgnore] public string DurationText => LastSeconds is long seconds ? FormatDuration(seconds) : "—";
    [JsonIgnore] public string UpdatedText => UpdatedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—";
    [JsonIgnore] public string StatusText => State switch
    {
        QueryState.Success => "查询成功", QueryState.Running => "正在查询", QueryState.Failed => "查询失败", _ => "待查询"
    };
    [JsonIgnore] public string DetailText => string.IsNullOrEmpty(Error) ? (State != QueryState.Success && LastSeconds != null ? "显示上次成功结果，不计入本轮合计" : "") : Error;
    public void Refresh() { Raise(""); }
    public static string FormatDuration(long seconds) => $"{seconds / 3600:N0} 小时 {seconds % 3600 / 60:00} 分 {seconds % 60:00} 秒";
}

public record Account(long Uid, string Name);
public record QrTicket(string Url, string Key);
public enum QrState { Waiting, Scanned, Expired, Confirmed }
public record WatchResult(long Uid, string Name, long Seconds);
public record Candidate(long Uid, string Name, string Source);
public record SourceProgress(string Source, int Count, string State);
public sealed class AccountData
{
    public Account Account { get; set; } = new(0, "");
    public Guid? RunId { get; set; }
    public List<Anchor> Anchors { get; set; } = [];
    public List<SourceProgress> Sources { get; set; } = [];
}
public record Totals(long Seconds, int Success, int Failed, int Pending)
{
    public static Totals From(IEnumerable<Anchor> anchors, Guid? run)
    {
        var rows = anchors.ToList();
        return new(rows.Where(a => run != null && a.SuccessRun == run && a.State == QueryState.Success).Sum(a => a.LastSeconds ?? 0),
            rows.Count(a => run != null && a.SuccessRun == run && a.State == QueryState.Success),
            rows.Count(a => a.State == QueryState.Failed), rows.Count(a => a.State is QueryState.Pending or QueryState.Running));
    }
}

public class ApiException(string message, bool mustPause = false, bool loginExpired = false) : Exception(message)
{
    public bool MustPause { get; } = mustPause;
    public bool LoginExpired { get; } = loginExpired;
}

public static class JsonFields
{
    public static long? Number(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : null;
    }
    public static string Text(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public static JsonElement Required(this JsonElement e, string name, JsonValueKind kind)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new ApiException($"接口缺少有效的 {name} 字段");
        return value;
    }
}

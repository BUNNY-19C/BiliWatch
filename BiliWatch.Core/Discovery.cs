using System.Text.Json;

namespace BiliWatch.Core;

public sealed class Discovery(IBiliApi api)
{
    public static Anchor Merge(ICollection<Anchor> anchors, Candidate c)
    {
        if (c.Uid <= 0) throw new ArgumentException("UID 必须为正整数");
        var row = anchors.FirstOrDefault(a => a.Uid == c.Uid);
        if (row == null) { row = new Anchor { Uid = c.Uid, Name = string.IsNullOrEmpty(c.Name) ? $"UID {c.Uid}" : c.Name }; anchors.Add(row); }
        else if (!string.IsNullOrEmpty(c.Name)) row.Name = c.Name;
        row.Sources.Add(c.Source); row.Refresh(); return row;
    }

    public async Task RunAsync(long accountUid, Action<Candidate> found, Action<SourceProgress> report, CancellationToken token)
    {
        foreach (var source in new[] { "直播历史", "粉丝勋章", "全部关注" })
        {
            var ids = new HashSet<long>();
            void Add(Candidate c) { if (c.Uid > 0) { ids.Add(c.Uid); found(c); } }
            void Progress(string status) => report(new(source, ids.Count, status));
            Progress("获取中");
            try
            {
                if (source == "直播历史") await HistoryAsync(Add, () => Progress("获取中"), token);
                else await PagedAsync(source, accountUid, Add, () => Progress("获取中"), token);
                Progress("完成");
            }
            catch (OperationCanceledException) { Progress("已取消"); throw; }
            catch (ApiException e)
            {
                Progress(e.Message);
                if (e.MustPause) throw;
            }
        }
    }

    private async Task HistoryAsync(Action<Candidate> found, Action pageDone, CancellationToken token)
    {
        long max = 0, viewed = 0;
        var business = "";
        var visited = new HashSet<string>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add($"{max}:{viewed}:{business}")) throw new ApiException("分页游标重复，已停止；来源未完整获取");
            var d = await api.GetAsync($"https://api.bilibili.com/x/web-interface/history/cursor?type=live&ps=30&max={max}&view_at={viewed}&business={Uri.EscapeDataString(business)}", token);
            var list = d.Required("list", JsonValueKind.Array);
            if (list.GetArrayLength() == 0) return;
            foreach (var item in list.EnumerateArray())
            {
                var history = item.Required("history", JsonValueKind.Object);
                if (history.Text("business") != "live") continue;
                var uid = item.Number("author_mid") ?? 0;
                if (uid <= 0) throw new ApiException("历史记录缺少主播 UID；来源未完整获取");
                found(new(uid, item.Text("author_name"), "直播历史"));
            }
            pageDone();
            var cursor = d.Required("cursor", JsonValueKind.Object);
            max = cursor.Number("max") ?? throw new ApiException("历史分页缺少 max");
            viewed = cursor.Number("view_at") ?? throw new ApiException("历史分页缺少 view_at");
            business = cursor.Text("business");
            if (max == 0 && viewed == 0) return;
        }
    }

    private async Task PagedAsync(string source, long accountUid, Action<Candidate> found, Action pageDone, CancellationToken token)
    {
        var seen = new HashSet<long>();
        for (var page = 1; ; page++)
        {
            token.ThrowIfCancellationRequested();
            var medal = source == "粉丝勋章";
            var url = medal ? $"https://api.live.bilibili.com/xlive/app-ucenter/v1/user/GetMyMedals?page={page}&page_size=10"
                : $"https://api.bilibili.com/x/relation/followings?vmid={accountUid}&pn={page}&ps=50&order=desc&order_type=attention";
            var d = await api.GetAsync(url, token);
            var list = d.Required(medal ? "items" : "list", JsonValueKind.Array);
            var total = d.Number(medal ? "count" : "total");
            if (list.GetArrayLength() == 0)
            {
                if (total > seen.Count) throw new ApiException("接口提前返回空页；来源未完整获取");
                return;
            }
            var before = seen.Count;
            foreach (var item in list.EnumerateArray())
            {
                var uid = item.Number(medal ? "target_id" : "mid") ?? 0;
                if (uid <= 0) throw new ApiException("列表缺少主播 UID；来源未完整获取");
                seen.Add(uid);
                var name = item.Text(medal ? "target_name" : "uname");
                found(new(uid, name, source));
            }
            pageDone();
            if (seen.Count == before) throw new ApiException("分页内容重复，已停止；来源未完整获取");
            if (total != null && seen.Count >= total) return;
            if (medal && d.TryGetProperty("page_info", out var info) && info.Number("total_page") is long pages && page >= pages)
            {
                if (total > seen.Count) throw new ApiException("列表数量不足；来源未完整获取");
                return;
            }
        }
    }
}

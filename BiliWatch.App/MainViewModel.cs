using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using BiliWatch.Core;

namespace BiliWatch.App;

public sealed class MainViewModel : Observable, IDisposable
{
    private BiliClient api = new();
    private readonly LocalStore store = new();
    private Account? account;
    private Guid? run;
    private CancellationTokenSource? operation;
    private bool authenticated, busy;
    private string status = "登录后，发现你看过的直播间。", search = "", manualUid = "";
    private Totals totals = new(0, 0, 0, 0);
    private DateTimeOffset lastSaved;
    public ObservableCollection<Anchor> Anchors { get; } = [];
    public ObservableCollection<SourceProgress> Sources { get; } = [];
    public ObservableCollection<RankRow> Ranking { get; } = [];
    public ICollectionView Rows { get; }
    public string AccountText => account == null ? "尚未登录" : $"{account.Name}  /  UID {account.Uid}";
    public string LoginText => authenticated ? "已登录 · 登录状态保存在本机" : account == null ? "扫码连接你的 B 站账号" : "离线缓存 · 扫码后可更新";
    public string Status { get => status; set => Set(ref status, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Raise(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanEdit => !Busy;
    public bool HasRows => Anchors.Count > 0;
    public bool Empty => !HasRows;
    public bool HasRanking => Ranking.Count > 0;
    public bool NoSearchResults => HasRows && !string.IsNullOrWhiteSpace(Search) && Rows.IsEmpty;
    public string Search { get => search; set { if (Set(ref search, value)) { Rows.Refresh(); Raise(nameof(NoSearchResults)); } } }
    public string ManualUid { get => manualUid; set => Set(ref manualUid, value); }
    public string Hours => (totals.Seconds / 3600d).ToString("N2");
    public string ExactDuration => Anchor.FormatDuration(totals.Seconds);
    public int SuccessCount => totals.Success;
    public int FailedCount => totals.Failed;
    public int PendingCount => totals.Pending;
    public string ProgressText => $"已处理 {totals.Success + totals.Failed:N0} / {Anchors.Count:N0} 位主播";
    public double Progress => Anchors.Count == 0 ? 0 : (totals.Success + totals.Failed) * 100d / Anchors.Count;
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddCommand { get; }

    public MainViewModel()
    {
        Rows = CollectionViewSource.GetDefaultView(Anchors);
        Rows.CollectionChanged += (_, _) => Raise(nameof(NoSearchResults));
        Rows.SortDescriptions.Add(new(nameof(Anchor.LastSeconds), ListSortDirection.Descending));
        Rows.Filter = obj => obj is Anchor a && (string.IsNullOrWhiteSpace(Search) || a.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase) || a.Uid.ToString().Contains(Search));
        LoginCommand = new AsyncCommand(LoginAsync, ShowError, () => !Busy);
        LogoutCommand = new Command(Logout, () => !Busy && account != null);
        StartCommand = new AsyncCommand(() => QueryAsync(false, false), ShowError, () => !Busy && authenticated);
        RetryCommand = new AsyncCommand(() => QueryAsync(true, false), ShowError, () => !Busy && authenticated && FailedCount > 0);
        ResumeCommand = new AsyncCommand(() => QueryAsync(false, true), ShowError, () => !Busy && authenticated && PendingCount > 0 && run != null);
        CancelCommand = new Command(() => operation?.Cancel(), () => Busy && operation != null);
        AddCommand = new Command(AddUid, () => !Busy && account != null);
    }

    public async Task InitializeAsync()
    {
        Busy = true;
        try
        {
            var last = store.LastAccount();
            if (last != null) LoadAccount(last);
            var session = store.ReadSession();
            if (session == null) return;
            api.ImportCookies(session.Cookies);
            Status = "正在验证本机登录状态…";
            var current = await api.AccountAsync(CancellationToken.None);
            LoadAccount(current); authenticated = true;
            Status = "登录状态有效，可以开始查询。";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Busy = false; RefreshAccount(); }
    }

    private void LoadAccount(Account selected)
    {
        // Clear previous account before reading the next one, including when its cache is unreadable.
        account = selected; Anchors.Clear(); Sources.Clear(); run = null;
        var cached = store.Load(selected.Uid);
        if (cached != null)
        {
            run = cached.RunId;
            foreach (var row in cached.Anchors) Anchors.Add(row);
            foreach (var source in cached.Sources) Sources.Add(source);
        }
        RefreshStats(); RefreshAccount();
    }

    private async Task LoginAsync()
    {
        Busy = true;
        try
        {
            var login = new LoginWindow { Owner = Application.Current.MainWindow };
            if (login.ShowDialog() != true || login.AuthenticatedClient == null || login.Account == null) return;
            api.Dispose(); api = login.AuthenticatedClient;
            authenticated = true;
            LoadAccount(login.Account);
            store.SaveSession(login.Account, api.ExportCookies());
            Status = "扫码登录成功。点击“开始查询”自动寻找主播，或先补充 UID。";
            await Task.CompletedTask;
        }
        finally { Busy = false; RefreshAccount(); }
    }
    private void Logout()
    {
        try { store.ForgetSession(); }
        catch (Exception ex) { ShowError(ex); return; }
        api.Dispose(); api = new(); authenticated = false; account = null; run = null;
        Anchors.Clear(); Sources.Clear(); Status = "已退出本机登录；账号查询缓存仍保留。";
        RefreshStats(); RefreshAccount();
    }
    private void RefreshAccount() { Raise(nameof(AccountText)); Raise(nameof(LoginText)); CommandManager.InvalidateRequerySuggested(); }
    private void AddUid()
    {
        var tokens = ManualUid.Split([' ', ',', '，', ';', '；', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var ids = new List<long>();
        foreach (var value in tokens)
        {
            if (!long.TryParse(value, out var id) || id <= 0) { Status = "请输入主播个人空间的数字 UID，可用逗号分隔；不是直播间号。"; return; }
            ids.Add(id);
        }
        if (ids.Count == 0) { Status = "请先输入主播 UID。"; return; }
        foreach (var id in ids.Distinct()) Discovery.Merge(Anchors, new(id, "", "手动补充"));
        ManualUid = ""; RefreshStats(); Save(); Status = $"已补充 {ids.Distinct().Count()} 个 UID；点击开始查询或继续待查询项。";
    }

    private async Task QueryAsync(bool retry, bool resume)
    {
        if (account == null) return;
        Busy = true; operation = new(); CommandManager.InvalidateRequerySuggested();
        try
        {
            if (!retry && !resume)
            {
                run = Guid.NewGuid(); QueryRunner.Begin(Anchors); Sources.Clear(); RefreshStats(); Save();
                Status = "正在发现主播：直播历史 → 粉丝勋章 → 全部关注…";
                await new Discovery(api).RunAsync(account.Uid, c => { Discovery.Merge(Anchors, c); RefreshStats(); }, p =>
                {
                    var previous = Sources.FirstOrDefault(s => s.Source == p.Source);
                    if (previous != null) Sources[Sources.IndexOf(previous)] = p; else Sources.Add(p);
                    SavePeriodically();
                }, operation.Token);
                Save();
            }
            run ??= Guid.NewGuid();
            var selected = Anchors.Where(a => retry ? a.State == QueryState.Failed : a.State == QueryState.Pending).ToArray();
            Status = $"正在逐个查询 {selected.Length:N0} 位主播，可随时取消。";
            await new QueryRunner(api).RunAsync(selected, run.Value, () => { RefreshStats(); SavePeriodically(); }, operation.Token);
            Status = Anchors.Count == 0 ? "没有发现主播。可以手动补充主播 UID 后查询。"
                : $"本轮已完成：成功 {SuccessCount:N0}，失败 {FailedCount:N0}。" + (Sources.Any(s => s.State != "完成") ? " 部分来源未完整获取，请查看来源状态。" : "");
        }
        catch (OperationCanceledException) { Status = "已取消，成功结果已保留；可继续待查询项。"; }
        catch (ApiException ex)
        {
            Status = ex.Message;
            if (ex.LoginExpired) { authenticated = false; RefreshAccount(); }
        }
        finally
        {
            operation.Dispose(); operation = null; Busy = false; RefreshStats(); Save(); Rows.Refresh();
        }
    }

    private void SavePeriodically()
    {
        if (DateTimeOffset.Now - lastSaved > TimeSpan.FromSeconds(3)) Save();
    }
    private void Save()
    {
        if (account == null) return;
        store.Save(new AccountData { Account = account, RunId = run, Anchors = Anchors.ToList(), Sources = Sources.ToList() });
        lastSaved = DateTimeOffset.Now;
    }
    private void RefreshStats()
    {
        totals = Totals.From(Anchors, run);
        Ranking.Clear();
        var top = Anchors.Where(a => run != null && a.SuccessRun == run && a.State == QueryState.Success).OrderByDescending(a => a.LastSeconds).ThenBy(a => a.Uid).Take(10).ToArray();
        var highest = Math.Max(1, top.FirstOrDefault()?.LastSeconds ?? 0);
        for (var i = 0; i < top.Length; i++) Ranking.Add(new(i + 1, top[i].Name, (top[i].LastSeconds!.Value / 3600d).ToString("N2") + " h", top[i].LastSeconds!.Value * 100d / highest, top[i].DurationText));
        Raise(nameof(Hours)); Raise(nameof(ExactDuration)); Raise(nameof(SuccessCount)); Raise(nameof(FailedCount)); Raise(nameof(PendingCount)); Raise(nameof(Progress)); Raise(nameof(ProgressText)); Raise(nameof(HasRows)); Raise(nameof(Empty)); Raise(nameof(HasRanking));
        CommandManager.InvalidateRequerySuggested();
    }
    public void ShowError(Exception ex)
    {
        Status = ex switch
        {
            ApiException => ex.Message,
            IOException => "无法读写本机缓存，请检查磁盘空间和目录权限。",
            System.Text.Json.JsonException or InvalidDataException => "本机缓存格式异常；请保留缓存文件并检查。",
            System.ComponentModel.Win32Exception => "本机登录状态无法解密，请重新扫码。",
            _ => $"操作未完成（{ex.GetType().Name}）。请重试。"
        };
    }
    public void Dispose() { operation?.Cancel(); if (!Busy) api.Dispose(); }
}
public record RankRow(int Rank, string Name, string Hours, double Percent, string Duration);

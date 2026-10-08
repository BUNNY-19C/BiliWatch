using System.Net;
using System.Text;
using System.Text.Json;
using BiliWatch.Core;

if (args.Contains("--live"))
{
    var store = new LocalStore();
    var session = store.ReadSession();
    if (session == null) { Console.WriteLine("LIVE BLOCKED: no local QR login session."); return 2; }
    using var api = new BiliClient(); api.ImportCookies(session.Cookies);
    var account = await api.AccountAsync(CancellationToken.None);
    var d = await api.GetAsync("https://api.live.bilibili.com/xlive/app-ucenter/v1/user/GetMyMedals?page=1&page_size=10", CancellationToken.None);
    var ids = d.Required("items", JsonValueKind.Array).EnumerateArray().Select(i => i.Number("target_id") ?? 0).Where(id => id > 0).Distinct().Take(3).ToArray();
    var results = new List<object>(); var anchors = new List<Anchor>(); var run = Guid.NewGuid();
    foreach (var uid in ids)
    {
        var result = await api.WatchAsync(uid, CancellationToken.None);
        var anchor = new Anchor { Uid = uid, Name = result.Name, LastSeconds = result.Seconds, SuccessRun = run, State = QueryState.Success };
        anchors.Add(anchor);
        results.Add(new { Uid = uid, result.Name, RawSeconds = result.Seconds, Hours = result.Seconds / 3600d, Display = anchor.DurationText });
    }
    var total = Totals.From(anchors, run);
    var report = JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, AccountUid = account.Uid, Verified = results.Count, Results = results, TotalSeconds = total.Seconds, TotalHours = total.Seconds / 3600d }, new JsonSerializerOptions { WriteIndented = true });
    var output = args.SkipWhile(a => a != "--report").Skip(1).FirstOrDefault();
    if (output != null) File.WriteAllText(output, report);
    Console.WriteLine(report);
    return results.Count == 3 ? 0 : 2;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void TestAsync(string name, Func<Task> run) => tests.Add((name, run));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
JsonElement J(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
async Task Throws<T>(Func<Task> run) where T : Exception
{
    try { await run(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
Test("UID merge: multiple sources, one row", () =>
{
    var rows = new List<Anchor>();
    Discovery.Merge(rows, new(101, "测试主播", "直播历史"));
    Discovery.Merge(rows, new(101, "测试主播", "粉丝勋章"));
    Check(rows.Count == 1 && rows[0].Sources.Count == 2);
});
Test("Watch: zero is success; seconds format is exact", () =>
{
    Check(BiliClient.ParseWatch(101, J("{\"ruid\":101,\"watch_time\":0}")).Seconds == 0);
    Check(BiliClient.ParseWatch(101, J("{\"watch_time\":\"3661\"}")).Seconds == 3661);
    Check(Anchor.FormatDuration(3661) == "1 小时 01 分 01 秒");
});
TestAsync("Watch: missing, negative, fractional and mismatched values rejected", async () =>
{
    foreach (var json in new[] { "{}", "{\"watch_time\":null}", "{\"watch_time\":-1}", "{\"watch_time\":0.5}", "{\"watch_time\":1,\"ruid\":102}" })
        await Throws<ApiException>(() => Task.FromResult(BiliClient.ParseWatch(101, J(json))));
});
Test("QR: waiting, scanned, confirmed and expired", () =>
{
    Check(BiliClient.ParseQrState(J("{\"code\":86101}")) == QrState.Waiting);
    Check(BiliClient.ParseQrState(J("{\"code\":86090}")) == QrState.Scanned);
    Check(BiliClient.ParseQrState(J("{\"code\":86038}")) == QrState.Expired);
    Check(BiliClient.ParseQrState(J("{\"code\":0}")) == QrState.Confirmed);
});
Test("Totals: stale results excluded; zero counted as successful", () =>
{
    var current = Guid.NewGuid();
    var rows = new[] {
        new Anchor { LastSeconds = 3600, SuccessRun = current, State = QueryState.Success },
        new Anchor { LastSeconds = 0, SuccessRun = current, State = QueryState.Success },
        new Anchor { LastSeconds = 9999, SuccessRun = Guid.NewGuid(), State = QueryState.Pending },
        new Anchor { LastSeconds = 5000, SuccessRun = current, State = QueryState.Failed }};
    var t = Totals.From(rows, current);
    Check(t == new Totals(3600, 2, 1, 1));
    QueryRunner.Begin(rows); Check(Totals.From(rows, Guid.NewGuid()) == new Totals(0, 0, 0, 4));
    Check(rows[0].LastSeconds == 3600);
});
TestAsync("Batch: failure preserves old value, retry replaces exactly once", async () =>
{
    var run = Guid.NewGuid(); var rows = new[] { new Anchor { Uid = 1 }, new Anchor { Uid = 2, LastSeconds = 99 } };
    var fake = new FakeApi { Watch = (uid, ct) => uid == 1 ? Task.FromResult(new WatchResult(uid, "A", 0)) : throw new ApiException("No watch_time") };
    await new QueryRunner(fake).RunAsync(rows, run, () => { }, CancellationToken.None);
    Check(rows[1].LastSeconds == 99 && Totals.From(rows, run) == new Totals(0, 1, 1, 0));
    fake.Watch = (uid, ct) => Task.FromResult(new WatchResult(uid, "B", 3600));
    await new QueryRunner(fake).RunAsync(rows.Where(a => a.State == QueryState.Failed), run, () => { }, CancellationToken.None);
    Check(Totals.From(rows, run) == new Totals(3600, 2, 0, 0));
});
TestAsync("Batch: cancellation preserves completed rows and leaves pending rows", async () =>
{
    using var cts = new CancellationTokenSource(); var run = Guid.NewGuid();
    var rows = new[] { new Anchor { Uid = 1 }, new Anchor { Uid = 2 }, new Anchor { Uid = 3 } };
    var fake = new FakeApi { Watch = (uid, ct) => { if (uid == 2) { cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.FromResult(new WatchResult(uid, "A", 10)); } };
    await Throws<OperationCanceledException>(() => new QueryRunner(fake).RunAsync(rows, run, () => { }, cts.Token));
    Check(Totals.From(rows, run) == new Totals(10, 1, 0, 2));
});
TestAsync("Batch: risk/auth errors pause remaining requests", async () =>
{
    var calls = 0; var rows = new[] { new Anchor { Uid = 1 }, new Anchor { Uid = 2 } };
    var fake = new FakeApi { Watch = (uid, ct) => { calls++; throw new ApiException("Expired", true, true); } };
    await Throws<ApiException>(() => new QueryRunner(fake).RunAsync(rows, Guid.NewGuid(), () => { }, CancellationToken.None));
    Check(calls == 1 && rows[0].State == QueryState.Failed && rows[1].State == QueryState.Pending);
});
TestAsync("Discovery: pagination, dedup, final empty history", async () =>
{
    var historyCalls = 0; var rows = new List<Anchor>(); var sources = new List<SourceProgress>();
    var fake = new FakeApi { Get = (url, ct) => Task.FromResult(J(url.Contains("history/cursor") ? ++historyCalls == 1
        ? "{\"list\":[{\"history\":{\"business\":\"live\"},\"author_mid\":101,\"author_name\":\"A\"}],\"cursor\":{\"max\":12,\"view_at\":100,\"business\":\"live\"}}"
        : "{\"list\":[]}"
        : url.Contains("GetMyMedals") ? "{\"count\":1,\"items\":[{\"target_id\":101,\"target_name\":\"A\"}],\"page_info\":{\"total_page\":1}}"
        : url.Contains("pn=1&") ? "{\"total\":2,\"list\":[{\"mid\":101,\"uname\":\"A\"}]}"
        : "{\"total\":2,\"list\":[{\"mid\":102,\"uname\":\"B\"}]}")) };
    await new Discovery(fake).RunAsync(10, c => Discovery.Merge(rows, c), sources.Add, CancellationToken.None);
    Check(rows.Count == 2 && rows[0].Sources.Count == 3 && historyCalls == 2);
    Check(sources.Count(s => s.State == "完成") == 3);
});
TestAsync("Discovery: repeated cursor stops, next source still runs", async () =>
{
    var calls = 0; var sources = new List<SourceProgress>();
    var fake = new FakeApi { Get = (url, ct) => Task.FromResult(J(url.Contains("history/cursor")
        ? (++calls > 0 ? "{\"list\":[{\"history\":{\"business\":\"live\"},\"author_mid\":101}],\"cursor\":{\"max\":12,\"view_at\":100,\"business\":\"live\"}}" : "{}")
        : url.Contains("GetMyMedals") ? "{\"count\":0,\"items\":[]}" : "{\"total\":0,\"list\":[]}")) };
    await new Discovery(fake).RunAsync(10, _ => { }, sources.Add, CancellationToken.None);
    Check(calls == 2 && sources.Any(s => s.State.Contains("游标重复")) && sources.Count(s => s.State == "完成") == 2);
});
TestAsync("Discovery: repeated pages and premature empty pages marked partial", async () =>
{
    foreach (var repeat in new[] { true, false })
    {
        var sources = new List<SourceProgress>();
        var fake = new FakeApi { Get = (url, ct) => Task.FromResult(J(url.Contains("history/cursor") ? "{\"list\":[]}"
            : url.Contains("GetMyMedals") ? "{\"count\":0,\"items\":[]}"
            : url.Contains("pn=1&") || repeat ? "{\"total\":2,\"list\":[{\"mid\":101}]}" : "{\"total\":2,\"list\":[]}")) };
        await new Discovery(fake).RunAsync(10, _ => { }, sources.Add, CancellationToken.None);
        Check(sources.Last().State.Contains("未完整获取"));
    }
});
TestAsync("HTTP: two timeout retries, third success", async () =>
{
    var handler = new StubHandler((n, request) => n < 3 ? throw new TaskCanceledException() : Response("{\"code\":0,\"data\":{\"watch_time\":42}}"));
    using var api = new BiliClient(handler: handler, requestInterval: TimeSpan.Zero);
    Check((await api.WatchAsync(1, CancellationToken.None)).Seconds == 42 && handler.Calls == 3);
});
TestAsync("HTTP: retries stop after three attempts", async () =>
{
    var handler = new StubHandler((n, request) => throw new TaskCanceledException());
    using var api = new BiliClient(handler: handler, requestInterval: TimeSpan.Zero);
    await Throws<ApiException>(() => api.WatchAsync(1, CancellationToken.None)); Check(handler.Calls == 3);
});
TestAsync("HTTP: user cancellation is not retried", async () =>
{
    using var cts = new CancellationTokenSource();
    var handler = new StubHandler((n, request) => { cts.Cancel(); throw new TaskCanceledException(); });
    using var api = new BiliClient(handler: handler, requestInterval: TimeSpan.Zero);
    await Throws<OperationCanceledException>(() => api.WatchAsync(1, cts.Token)); Check(handler.Calls == 1);
});
TestAsync("HTTP: expired login / risk flags and invalid JSON", async () =>
{
    foreach (var code in new[] { -101, -352, -412, -509 })
    {
        using var api = new BiliClient(handler: new StubHandler((n, r) => Response($"{{\"code\":{code}}}")), requestInterval: TimeSpan.Zero);
        try { await api.WatchAsync(1, CancellationToken.None); throw new Exception("Expected API error"); }
        catch (ApiException e) { Check(e.MustPause && e.LoginExpired == (code == -101)); }
    }
    using var invalid = new BiliClient(handler: new StubHandler((n, r) => Response("<html/>")), requestInterval: TimeSpan.Zero);
    await Throws<ApiException>(() => invalid.WatchAsync(1, CancellationToken.None));
});
TestAsync("HTTP: allowlist blocks user supplied hosts and redirects", async () =>
{
    var handler = new StubHandler((n, r) => new HttpResponseMessage(HttpStatusCode.Redirect));
    using var api = new BiliClient(handler: handler, requestInterval: TimeSpan.Zero);
    foreach (var url in new[] { "http://api.bilibili.com/", "https://localhost/", "https://127.0.0.1/", "https://192.168.1.1/", "https://api.bilibili.com.evil.example/" })
        await Throws<ApiException>(() => api.GetAsync(url, CancellationToken.None));
    Check(handler.Calls == 0);
    await Throws<ApiException>(() => api.WatchAsync(1, CancellationToken.None)); Check(handler.Calls == 1);
});
TestAsync("QR: current account domain accepted", async () =>
{
    var handler = new StubHandler((n, r) => Response("{\"code\":0,\"data\":{\"url\":\"https://account.bilibili.com/h5/account-h5/auth/scan-web\",\"qrcode_key\":\"fixture-not-a-real-key\"}}"));
    using var api = new BiliClient(handler: handler, requestInterval: TimeSpan.Zero);
    Check((await api.CreateQrAsync(CancellationToken.None)).Url.StartsWith("https://account.bilibili.com/"));
});
Test("Storage: separate accounts, cached run and interrupted state", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "BiliWatch-test-" + Guid.NewGuid());
    try
    {
        var store = new LocalStore(root); var run = Guid.NewGuid();
        store.Save(new AccountData { Account = new(101, "A"), RunId = run, Anchors = [new Anchor { Uid = 1, State = QueryState.Running, LastSeconds = 99 }] });
        store.Save(new AccountData { Account = new(102, "B"), Anchors = [new Anchor { Uid = 2 }] });
        Check(store.Load(101)!.Anchors.Single().State == QueryState.Pending && store.Load(101)!.RunId == run);
        Check(store.Load(102)!.Anchors.Single().Uid == 2 && store.Load(103) == null);
        store.SaveSession(new(101, "A"), []);
        Check(store.ReadSession()!.Account.Uid == 101 && store.LastAccount()!.Uid == 101);
        var raw = File.ReadAllBytes(Path.Combine(root, "session.bin"));
        Check(!Encoding.UTF8.GetString(raw).Contains("Cookies"));
        store.ForgetSession(); Check(store.ReadSession() == null && store.LastAccount() == null && store.Load(101) != null);
    }
    finally { Directory.Delete(root, true); }
});
Test("DPAPI: roundtrip and corrupted input", () =>
{
    var plain = Encoding.UTF8.GetBytes("non-secret-fixture");
    var cipher = Dpapi.Protect(plain); Check(!cipher.SequenceEqual(plain)); Check(Dpapi.Unprotect(cipher).SequenceEqual(plain));
    try { Dpapi.Unprotect([0, 1, 2]); throw new Exception("Expected decryption failure"); } catch (System.ComponentModel.Win32Exception) { }
});

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + e.Message); }
}
Console.WriteLine($"RESULT {tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
sealed class StubHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(++Calls, request)); }
}
sealed class FakeApi : IBiliApi
{
    public Func<string, CancellationToken, Task<JsonElement>> Get { get; set; } = (_, _) => throw new NotImplementedException();
    public Func<long, CancellationToken, Task<WatchResult>> Watch { get; set; } = (_, _) => throw new NotImplementedException();
    public Task<JsonElement> GetAsync(string url, CancellationToken token) => Get(url, token);
    public Task<WatchResult> WatchAsync(long uid, CancellationToken token) => Watch(uid, token);
}

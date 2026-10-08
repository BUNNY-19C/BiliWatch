using System.Net;
using System.Text.Json;

namespace BiliWatch.Core;

public interface IBiliApi
{
    Task<JsonElement> GetAsync(string url, CancellationToken token);
    Task<WatchResult> WatchAsync(long uid, CancellationToken token);
}

public sealed class BiliClient : IBiliApi, IDisposable
{
    private readonly HttpClient http;
    private readonly CookieContainer cookies;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan interval;
    private DateTimeOffset nextRequest;
    private static readonly HashSet<string> Hosts = ["api.bilibili.com", "api.live.bilibili.com", "passport.bilibili.com"];
    private static readonly Uri CookieOrigin = new("https://api.bilibili.com");

    public BiliClient(CookieContainer? jar = null, HttpMessageHandler? handler = null, TimeSpan? requestInterval = null)
    {
        cookies = jar ?? new CookieContainer();
        http = new HttpClient(handler ?? new HttpClientHandler
        {
            CookieContainer = cookies, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        interval = requestInterval ?? TimeSpan.FromSeconds(1);
    }

    public async Task<JsonElement> GetAsync(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !Hosts.Contains(uri.Host) || uri.Port != 443 || uri.UserInfo != "")
            throw new ApiException("请求地址不在 B 站接口白名单内");
        await gate.WaitAsync(token);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = nextRequest - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                nextRequest = DateTimeOffset.UtcNow + interval;
                try
                {
                    using var response = await http.GetAsync(uri, token);
                    if (response.StatusCode == HttpStatusCode.Unauthorized) throw new ApiException("登录已失效，请重新扫码", true, true);
                    if ((int)response.StatusCode is 403 or 412 or 429) throw new ApiException($"B 站限制了请求（HTTP {(int)response.StatusCode}），任务已暂停，请稍后重试", true);
                    if (!response.IsSuccessStatusCode) throw new ApiException($"接口请求失败（HTTP {(int)response.StatusCode}）");
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    var root = doc.RootElement;
                    var code = root.Number("code") ?? throw new ApiException("接口返回格式已变化：缺少 code");
                    if (code is -101 or -111 or 61000) throw new ApiException($"登录已失效（{code}），请重新扫码", true, true);
                    if (code is -352 or -412 or -509) throw new ApiException($"B 站限制了请求（{code}），任务已暂停，请稍后重试", true);
                    // Do not expose raw response messages: a response can contain authentication material.
                    if (code != 0) throw new ApiException($"B 站接口返回错误码 {code}");
                    return root.Required("data", JsonValueKind.Object).Clone();
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (attempt >= 2) throw new ApiException("网络超时，已重试两次");
                }
                catch (HttpRequestException)
                {
                    if (attempt >= 2) throw new ApiException("网络连接失败，已重试两次");
                }
                catch (JsonException) { throw new ApiException("接口返回了非 JSON 内容，请稍后重试"); }
            }
        }
        finally { gate.Release(); }
    }

    public async Task<QrTicket> CreateQrAsync(CancellationToken token)
    {
        var d = await GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", token);
        var url = d.Text("url"); var key = d.Text("qrcode_key");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || (u.Host != "passport.bilibili.com" && u.Host != "account.bilibili.com") || key.Length == 0)
            throw new ApiException("二维码接口返回格式已变化");
        return new(url, key);
    }

    public async Task<QrState> PollQrAsync(string key, CancellationToken token)
    {
        var d = await GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key), token);
        return ParseQrState(d);
    }
    public static QrState ParseQrState(JsonElement data) => data.Number("code") switch
    {
        0 => QrState.Confirmed, 86101 => QrState.Waiting, 86090 => QrState.Scanned, 86038 => QrState.Expired,
        _ => throw new ApiException("二维码状态无法识别，请重新获取")
    };
    public async Task<Account> AccountAsync(CancellationToken token)
    {
        var d = await GetAsync("https://api.bilibili.com/x/web-interface/nav", token);
        if (!d.TryGetProperty("isLogin", out var login) || login.ValueKind != JsonValueKind.True || d.Number("mid") is not > 0)
            throw new ApiException("登录未生效，请重新扫码", true, true);
        return new(d.Number("mid")!.Value, d.Text("uname"));
    }
    public async Task<WatchResult> WatchAsync(long uid, CancellationToken token)
    {
        if (uid <= 0) throw new ArgumentOutOfRangeException(nameof(uid));
        var d = await GetAsync($"https://api.live.bilibili.com/xlive/general-interface/v1/guard/GuardActive?platform=android&ruid={uid}", token);
        return ParseWatch(uid, d);
    }
    public static WatchResult ParseWatch(long uid, JsonElement d)
    {
        var seconds = d.Number("watch_time");
        if (seconds is null or < 0) throw new ApiException("接口未返回有效观看时长（watch_time），不计入合计");
        if (d.Number("ruid") is long actual && actual != uid) throw new ApiException("接口返回的主播 UID 不匹配");
        return new(uid, d.Text("rusername"), seconds.Value);
    }
    public List<StoredCookie> ExportCookies() => cookies.GetCookies(CookieOrigin).Cast<Cookie>()
        .Where(c => !c.Expired).Select(c => new StoredCookie(c.Name, c.Value, c.Domain, c.Path, c.Expires, c.Secure, c.HttpOnly)).ToList();
    public void ImportCookies(IEnumerable<StoredCookie> saved)
    {
        foreach (var c in saved)
        {
            if (c.Domain.TrimStart('.') != "bilibili.com") continue;
            cookies.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain) { Expires = c.Expires, Secure = c.Secure, HttpOnly = c.HttpOnly });
        }
    }
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
public record StoredCookie(string Name, string Value, string Domain, string Path, DateTime Expires, bool Secure, bool HttpOnly);

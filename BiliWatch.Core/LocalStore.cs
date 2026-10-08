using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BiliWatch.Core;

public sealed class LocalStore
{
    public string Root { get; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public LocalStore(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("BILIWATCH_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliWatch");
    }
    private string AccountPath(long uid)
    {
        if (uid <= 0) throw new ArgumentOutOfRangeException(nameof(uid));
        return Path.Combine(Root, $"account-{uid}.json");
    }
    public AccountData? Load(long uid)
    {
        var path = AccountPath(uid);
        if (!File.Exists(path)) return null;
        var result = JsonSerializer.Deserialize<AccountData>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("缓存为空");
        if (result.Account.Uid != uid) throw new InvalidDataException("缓存账号不匹配");
        foreach (var row in result.Anchors)
            if (row.State == QueryState.Running) row.State = QueryState.Pending;
        return result;
    }
    public void Save(AccountData data) => Write(AccountPath(data.Account.Uid), JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions));
    public void SaveSession(Account account, List<StoredCookie> cookies)
    {
        Write(Path.Combine(Root, "session.bin"), Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(new Session(account, cookies))));
        Write(Path.Combine(Root, "last-account.json"), JsonSerializer.SerializeToUtf8Bytes(account));
    }
    public Session? ReadSession()
    {
        var path = Path.Combine(Root, "session.bin");
        return File.Exists(path) ? JsonSerializer.Deserialize<Session>(Dpapi.Unprotect(File.ReadAllBytes(path))) : null;
    }
    public Account? LastAccount()
    {
        var path = Path.Combine(Root, "last-account.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Account>(File.ReadAllText(path)) : null;
    }
    public void ForgetSession()
    {
        File.Delete(Path.Combine(Root, "session.bin"));
        File.Delete(Path.Combine(Root, "last-account.json"));
    }
    private void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Root);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }
}
public record Session(Account Account, List<StoredCookie> Cookies);

public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] data) => Transform(data, true);
    public static byte[] Unprotect(byte[] data) => Transform(data, false);
    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = encrypt ? CryptProtectData(ref input, "BiliWatch session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取本机加密登录状态，请重新扫码");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}

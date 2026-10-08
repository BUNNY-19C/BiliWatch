using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using BiliWatch.Core;
using QRCoder;

namespace BiliWatch.App;

public partial class LoginWindow : Window
{
    private readonly BiliClient client = new();
    private CancellationTokenSource? attempt;
    private bool handedOff, closed;
    public BiliClient? AuthenticatedClient { get; private set; }
    public Account? Account { get; private set; }
    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await BeginAsync();
        Closed += (_, _) => { closed = true; if (attempt != null) attempt.Cancel(); else if (!handedOff) client.Dispose(); };
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await BeginAsync();
    private async Task BeginAsync()
    {
        if (attempt != null) return;
        var cts = new CancellationTokenSource(); attempt = cts;
        RefreshButton.IsEnabled = false; QrImage.Source = null;
        try
        {
            StatusLabel.Text = "正在获取二维码…";
            var ticket = await client.CreateQrAsync(cts.Token);
            using var qrData = QRCodeGenerator.GenerateQrCode(ticket.Url, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(qrData);
            using var stream = new MemoryStream(qr.GetGraphic(8));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            QrImage.Source = bitmap;
            var until = DateTimeOffset.UtcNow.AddSeconds(180);
            while (DateTimeOffset.UtcNow < until)
            {
                StatusLabel.Text = "等待扫码…";
                var state = await client.PollQrAsync(ticket.Key, cts.Token);
                if (state == QrState.Expired) break;
                if (state == QrState.Confirmed)
                {
                    StatusLabel.Text = "正在验证账号…";
                    Account = await client.AccountAsync(cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    AuthenticatedClient = client; handedOff = true; DialogResult = true; return;
                }
                StatusLabel.Text = state == QrState.Scanned ? "已扫码，请在手机上确认登录" : $"等待扫码 · {Math.Max(0, (int)(until - DateTimeOffset.UtcNow).TotalSeconds)} 秒后过期";
                await Task.Delay(2000, cts.Token);
            }
            StatusLabel.Text = "二维码已过期，请点击刷新。"; QrImage.Source = null;
        }
        catch (OperationCanceledException) { }
        catch (ApiException ex) { StatusLabel.Text = ex.Message; }
        catch (Exception) { StatusLabel.Text = "无法完成登录，请刷新二维码重试。"; }
        finally
        {
            attempt = null; cts.Dispose(); RefreshButton.IsEnabled = true;
            if (closed && !handedOff) client.Dispose();
        }
    }
}

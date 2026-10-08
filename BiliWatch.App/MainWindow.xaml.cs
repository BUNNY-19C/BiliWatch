using System.ComponentModel;
using System.Windows;

namespace BiliWatch.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = vm;
        Loaded += async (_, _) => await vm.InitializeAsync();
        Closed += (_, _) => vm.Dispose();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (vm.Busy)
        {
            vm.CancelCommand.Execute(null); vm.Status = "正在停止，请任务结束后关闭窗口。"; e.Cancel = true;
        }
        base.OnClosing(e);
    }
}

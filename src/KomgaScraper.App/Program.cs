namespace KomgaScraper.App;

static class Program
{
    /// <summary>未处理异常写进文件日志（格式对齐界面日志，导出日志时一并带走）。</summary>
    private static void TryLogUnhandled(Exception ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"log-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][ERR ] 未处理异常：{ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n");
        }
        catch { /* 日志写不了就算了，别在异常处理器里再炸 */ }
    }

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // 全局异常兜底：任何未处理异常只弹友好提示，不出 JIT 调试对话框；
        // 同时落到 data\logs\log-日期.log——之前只弹窗不记录，出了问题无从排查
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            TryLogUnhandled(e.Exception);
            MessageBox.Show(e.Exception.Message, "发生错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                TryLogUnhandled(ex);
                MessageBox.Show(ex.Message, "发生严重错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        if (args.Contains("--dev-settings"))
        {
            // 开发自检：主窗口 + 非模态设置对话框同时展示，便于截图
            var form = new MainForm();
            form.Load += (_, _) => form.ShowSettingsDev();
            Application.Run(form);
        }
        else
        {
            Application.Run(new MainForm());
        }
    }
}
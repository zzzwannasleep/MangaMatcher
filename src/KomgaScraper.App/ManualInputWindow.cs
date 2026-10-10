using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KomgaScraper.App;

/// <summary>纠正匹配：手动输入正确的 Bangumi 条目链接或 ID（纯代码构建的小窗口）。</summary>
public sealed class ManualInputWindow : Window
{
    private readonly TextBox _txt = new()
    {
        Watermark = "如 https://bgm.tv/subject/123456 或直接输入数字 ID",
        MinWidth = 380,
    };

    public ManualInputWindow()
    {
        Title = "纠正匹配 — 输入正确的 Bangumi 条目";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, PingFang SC, Segoe UI");

        var ok = new Button { Content = "确定", MinWidth = 88, MinHeight = 30, Classes = { "primary" } };
        var cancel = new Button { Content = "取消", MinWidth = 88, MinHeight = 30, Classes = { "app" } };
        ok.Click += (_, _) => { if (_txt.Text?.Trim().Length > 0) Close(_txt.Text); };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "输入 Bangumi 条目链接（bgm.tv/subject/xxx）或数字 ID：" },
                _txt,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { ok, cancel },
                },
            },
        };
        _txt.AttachedToVisualTree += (_, _) => _txt.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && _txt.Text?.Trim().Length > 0) Close(_txt.Text);
            if (e.Key == Avalonia.Input.Key.Escape) Close(null);
        };
    }
}
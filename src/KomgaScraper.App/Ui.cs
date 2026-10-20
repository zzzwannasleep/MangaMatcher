using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace KomgaScraper.App;

/// <summary>
/// 轻量消息框与窗口通用工具（Avalonia 没有内置 MessageBox）。
/// </summary>
internal static class Ui
{
    /// <summary>
    /// 把控件属性绑定到主题资源（App.axaml 主题词典里的 key）。
    /// 深色/浅色切换时 DynamicResource 字典变化，绑定自动跟随——比取快照式取色可靠。
    /// </summary>
    public static void BindTheme(AvaloniaObject c, AvaloniaProperty property, string key)
        => c.Bind(property, Application.Current!.Resources.GetResourceObservable(key).ToBinding());
    /// <summary>模态消息框。okOnly=true 只有确定；否则 确定/取消。返回 true=确定。</summary>
    public static Task<bool> MessageAsync(Window? owner, string message, string title = "提示", bool okOnly = false, bool error = false)
    {
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            MinWidth = 340,
            MaxWidth = 560,
        };
        var msg = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 10),
            MaxWidth = 520,
        };
        var ok = new Button { Content = "确定", MinWidth = 92, MinHeight = 30, Classes = { "app" } };
        var cancel = new Button { Content = "取消", MinWidth = 92, MinHeight = 30, Classes = { "app" } };
        bool okClicked = false;
        ok.Click += (_, _) => { okClicked = true; win.Close(); };
        cancel.Click += (_, _) => win.Close();

        var tcs = new TaskCompletionSource<bool>();
        win.Closed += (_, _) => tcs.TrySetResult(okClicked);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 4, 20, 16),
        };
        buttons.Children.Add(ok);
        if (!okOnly) buttons.Children.Add(cancel);

        var panel = new StackPanel { Spacing = 2 };
        if (error)
        {
            var cross = new TextBlock
            {
                Text = "✕", FontWeight = FontWeight.Bold,
                Margin = new Thickness(20, 14, 0, -10), FontSize = 16,
            };
            BindTheme(cross, TextBlock.ForegroundProperty, "ErrorBrush");
            panel.Children.Add(cross);
        }
        panel.Children.Add(msg);
        panel.Children.Add(buttons);
        win.Content = panel;

        // 结果只能走自己的 tcs：按钮里只调了 win.Close()，而 Avalonia 的 ShowDialog<T>
        // 在没给对话框结果时会用 default(T) 兜底（Window.ShowCore 里 tcs.SetResult(_dialogResult ?? default)），
        // 也就是"确定"永远被当成 false —— 所有确认框都会静默取消，按钮看起来完全失灵。
        // 这里只用 ShowDialog 挡住主窗口（模态），返回值一律以 Closed 事件里的 okClicked 为准。
        if (owner is not null) _ = win.ShowDialog(owner);
        else win.Show();
        return tcs.Task;
    }

    public static Task InfoAsync(Window? owner, string message, string title = "完成")
        => MessageAsync(owner, message, title, okOnly: true);

    public static Task ErrorAsync(Window? owner, string message, string title = "错误")
        => MessageAsync(owner, message, title, okOnly: true, error: true);

    /// <summary>多选一对话框（替代 YesNoCancel）。返回按钮 value；关闭窗口 = null。</summary>
    public static async Task<string?> ChoiceAsync(Window? owner, string message, string title, params (string Text, string Value)[] choices)
    {
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            MinWidth = 360,
        };
        var tcs = new TaskCompletionSource<string?>();
        win.Closed += (_, _) => tcs.TrySetResult(null);

        var panel = new StackPanel { Margin = new Thickness(20, 18) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 500,
            Margin = new Thickness(0, 0, 0, 14),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        foreach (var (text, value) in choices)
        {
            var b = new Button { Content = text, MinWidth = 100, MinHeight = 30, Classes = { "app" } };
            b.Click += (_, _) => { tcs.TrySetResult(value); win.Close(); };
            buttons.Children.Add(b);
        }
        panel.Children.Add(buttons);
        win.Content = panel;

        // 同上：ShowDialog<string?> 的返回值恒为 null（按钮只 Close() 不给结果），
        // 会让"选择"永远等于取消。模态显示照旧，结果只认按钮写进 tcs 的值。
        if (owner is not null) _ = win.ShowDialog(owner);
        else win.Show();
        return await tcs.Task;
    }

    /// <summary>选择文件夹（StorageProvider API）。返回 null=取消。</summary>
    public static async Task<string?> PickFolderAsync(Window owner, string? startAt = null)
    {
        var files = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择文件夹",
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>选择文件。patterns 例：["*.png","*.jpg"]。</summary>
    public static async Task<string?> PickFileAsync(Window owner, string title, string[] patterns, string? startAt = null)
    {
        var opts = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = title,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("匹配的文件") { Patterns = patterns },
                new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*.*"] },
            ],
        };
        var files = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>窗口内嵌通知（替代托盘气泡，跨平台）：底部滑入 toast，3 秒自动消失。</summary>
    public static void Toast(Window owner, string message, bool error = false)
    {
        var toast = new Border
        {
            Background = new SolidColorBrush(Color.Parse(error ? "#D13438" : "#2E2E38")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 9),
            Child = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                MaxWidth = 460,
                TextWrapping = TextWrapping.Wrap,
            },
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("translateY(14px)"),
            Transitions =
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
                new TransformOperationsTransition { Property = Layoutable.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 52),
            IsHitTestVisible = false,
        };
        var host = new Panel { IsHitTestVisible = false };
        host.Children.Add(toast);
        if (owner.Content is Panel p) p.Children.Add(host);
        else if (owner.Content is Border b && b.Child is Panel bp) bp.Children.Add(host);
        // 滑入
        Dispatcher.UIThread.Post(() =>
        {
            toast.Opacity = 1;
            toast.RenderTransform = TransformOperations.Parse("translateY(0)");
        }, DispatcherPriority.Background);
        // 3 秒后淡出移除
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                toast.Opacity = 0;
                toast.RenderTransform = TransformOperations.Parse("translateY(14px)");
                await Task.Delay(260);
                host.Children.Remove(toast);
            });
        });
    }
}

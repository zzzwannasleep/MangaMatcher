namespace KomgaScraper.App;

/// <summary>纠正匹配：手动输入正确的 Bangumi 条目链接或 ID，顶替旧的错误匹配。</summary>
internal sealed class ManualMatchDialog : Form
{
    private readonly TextBox _txt = new() { Width = 380, PlaceholderText = "如 https://bgm.tv/subject/123456 或直接输入数字 ID" };

    public string Input => _txt.Text;

    public ManualMatchDialog()
    {
        Text = "纠正匹配 — 输入正确的 Bangumi 条目";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 130);
        Font = new Font("Microsoft YaHei UI", 9F);

        var label = new Label
        {
            Text = "输入 Bangumi 条目链接（bgm.tv/subject/xxx）或数字 ID：",
            AutoSize = true, Location = new Point(12, 15),
        };
        _txt.Location = new Point(12, 42);
        var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 80, Location = new Point(240, 80) };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80, Location = new Point(330, 80) };

        Controls.AddRange([label, _txt, btnOk, btnCancel]);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        // 高 DPI：布局等比放大
        float dpi = DeviceDpi / 96f;
        if (dpi > 1.01f) Scale(new SizeF(dpi, dpi));
    }
}
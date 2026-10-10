param(
    [Parameter(Mandatory=$true)][string]$Action,
    [string]$ProcName = 'KomgaScraper',
    [string]$Title = '',
    [string]$OutDir = '',
    [string]$Prefix = 'shot',
    [string]$BtnText = '',
    [int]$X = 40, [int]$Y = 40, [int]$W = 900, [int]$H = 620
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public delegate bool EnumChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    public struct R { public int L; public int T; public int Rt; public int B; }
    public static void MakeDpiAware() {
        try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
        catch { try { SetProcessDPIAware(); } catch { } }
    }
    public static System.Collections.Generic.List<IntPtr> List(uint pid) {
        var res = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) res.Add(h); return true; }, IntPtr.Zero);
        return res;
    }
    public static System.Collections.Generic.List<object[]> ChildList(IntPtr parent) {
        var res = new System.Collections.Generic.List<object[]>();
        EnumChildWindows(parent, (c, l) => {
            var sb = new StringBuilder(128);
            GetClassName(c, sb, 128);
            res.Add(new object[] { c, sb.ToString() });
            return true;
        }, IntPtr.Zero);
        return res;
    }
}
'@
Add-Type -TypeDefinition $src
[W]::MakeDpiAware() | Out-Null

function Get-TargetWindows {
    $result = @()
    foreach ($proc in (Get-Process | Where-Object { $_.ProcessName -like "$ProcName*" })) {
        foreach ($h in [W]::List([uint32]$proc.Id)) {
            if ([W]::GetWindowTextLength($h) -le 0) { continue }
            $sb = New-Object System.Text.StringBuilder 512
            [void][W]::GetWindowText($h, $sb, 512)
            $result += [pscustomobject]@{ H = $h; Title = $sb.ToString() }
        }
    }
    return $result
}

switch ($Action) {
    'shot' {
        New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
        $i = 0
        foreach ($t in (Get-TargetWindows)) {
            $r = New-Object W+R
            [void][W]::GetWindowRect($t.H, [ref]$r)
            $w = $r.Rt - $r.L; $ht = $r.B - $r.T
            if ($w -le 0 -or $ht -le 0) { continue }
            $bmp = New-Object System.Drawing.Bitmap $w, $ht
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)   # 屏幕真实像素（配合 front 置顶使用）
            $g.Dispose()
            $safe = $t.Title -replace '[\\/:*?"<>|]', '_'
            $out = Join-Path $OutDir ("{0}-{1}-{2}.png" -f $Prefix, $i, $safe)
            $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            Write-Output ("SAVED {0} rect=({1},{2} {3}x{4})" -f $out, $r.L, $r.T, $w, $ht)
            $i++
        }
        if ($i -eq 0) { Write-Output 'NO-WINDOW-FOUND' }
    }
    'resize' {
        $found = $false
        foreach ($t in (Get-TargetWindows)) {
            if ($t.Title.Contains($Title)) {
                [void][W]::SetWindowPos($t.H, [IntPtr]::Zero, $X, $Y, $W, $H, 0x0040)
                Write-Output ("RESIZED '{0}' -> {1}x{2}" -f $t.Title, $W, $H)
                $found = $true
            }
        }
        if (-not $found) { Write-Output "NO-WINDOW-TITLE '$Title'" }
    }
    'front' {
        foreach ($t in (Get-TargetWindows)) {
            if ($Title -and -not $t.Title.Contains($Title)) { continue }
            [void][W]::ShowWindow($t.H, 9)                       # SW_RESTORE
            [void][W]::SetWindowPos($t.H, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)  # TOPMOST, NOMOVE, NOSIZE
            Write-Output ("TOPMOST '{0}'" -f $t.Title)
        }
    }
    'lvc' {
        # 读取主窗口里 SysListView32 的列宽（LVM_GETCOLUMNWIDTH），定位表头异常
        foreach ($proc in (Get-Process | Where-Object { $_.ProcessName -like "$ProcName*" })) {
            foreach ($h in [W]::List([uint32]$proc.Id)) {
                $sb = New-Object System.Text.StringBuilder 256
                [void][W]::GetWindowText($h, $sb, 256)
                if ($sb.ToString() -notlike 'KomgaScraper*') { continue }
                $children = New-Object System.Collections.Generic.List[IntPtr]
                foreach ($child in [W]::ChildList($h)) {
                    if ($child[1] -like '*SysListView32*') { $children.Add($child[0]) }
                }
                foreach ($lv in $children) {
                    $count = [W]::SendMessage($lv, 0xF004, [IntPtr]::Zero, [IntPtr]::Zero)  # LVM_GETITEMCOUNT
                    $widths = @()
                    for ($i = 0; $i -lt 10; $i++) {
                        $w = [W]::SendMessage($lv, 0xF01D, [IntPtr]$i, [IntPtr]::Zero)      # LVM_GETCOLUMNWIDTH
                        $widths += [long]$w
                    }
                    Write-Output ("LV items={0} widths=[{1}]" -f $count, ($widths -join ','))
                }
            }
        }
    }
    'click' {
        # 按控件文字点击按钮（BM_CLICK 跨进程对系统消息有效）
        $hit = $false
        foreach ($t in (Get-TargetWindows)) {
            if ($Title -and -not $t.Title.Contains($Title)) { continue }
            foreach ($child in [W]::ChildList($t.H)) {
                if ($child[1] -notmatch 'BUTTON') { continue }
                $sb = New-Object System.Text.StringBuilder 256
                [void][W]::GetWindowText($child[0], $sb, 256)
                $txt = $sb.ToString()
                if ($BtnText -and $txt -like "*$BtnText*") {
                    [void][W]::SendMessage($child[0], 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)  # BM_CLICK
                    Write-Output ("CLICKED '{0}' on '{1}'" -f $txt, $t.Title)
                    $hit = $true
                }
            }
        }
        if (-not $hit) { Write-Output "NO-BUTTON '$BtnText'" }
    }
    'kill' {
        Get-Process | Where-Object { $_.ProcessName -like "$ProcName*" } | Stop-Process -Force
        Write-Output 'KILLED'
    }
}
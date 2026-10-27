<#
    Helpers for driving SSMS from the outside: foreground it, send keystrokes, click, and
    screenshot. Dot-source this, then call the functions.

    This is deliberately crude (SendKeys and screen coordinates rather than UI Automation)
    because the results grid is custom-drawn and its cells are easier to hit by position
    than to address through the accessibility tree.
#>

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

if (-not ('SqlResultToolkit.Native' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

namespace SqlResultToolkit
{
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

        public const uint LEFTDOWN = 0x0002;
        public const uint LEFTUP   = 0x0004;

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(80);
            mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(40);
            mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
        }
    }
}
'@
}

function Get-Ssms {
    Get-Process -Name Ssms -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
}

function Wait-Ssms {
    param([int]$TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Ssms
        if ($p) { return $p }
        Start-Sleep -Milliseconds 500
    }
    throw "SSMS did not present a main window within $TimeoutSeconds s."
}

function Focus-Ssms {
    $p = Wait-Ssms
    [void][SqlResultToolkit.Native]::ShowWindow($p.MainWindowHandle, 3)   # SW_MAXIMIZE
    [void][SqlResultToolkit.Native]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Milliseconds 400
    return $p
}

function Get-SsmsRect {
    $p = Wait-Ssms
    $r = New-Object SqlResultToolkit.RECT
    [void][SqlResultToolkit.Native]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    [PSCustomObject]@{ Left=$r.Left; Top=$r.Top; Right=$r.Right; Bottom=$r.Bottom; Width=$r.Right-$r.Left; Height=$r.Bottom-$r.Top }
}

function Send-Keys {
    param([Parameter(Mandatory)][string]$Keys, [int]$SettleMs = 600)
    [void](Focus-Ssms)
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $SettleMs
}

function Click-Screen {
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y, [int]$SettleMs = 500)
    [void](Focus-Ssms)
    [SqlResultToolkit.Native]::Click($X, $Y)
    Start-Sleep -Milliseconds $SettleMs
}

function Save-Screen {
    param([Parameter(Mandatory)][string]$Path, [switch]$FullScreen)

    if ($FullScreen) {
        $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $x = $bounds.X; $y = $bounds.Y; $w = $bounds.Width; $h = $bounds.Height
    } else {
        $r = Get-SsmsRect
        $x = $r.Left; $y = $r.Top; $w = $r.Width; $h = $r.Height
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Write-Host "screenshot: $Path  ($w x $h at $x,$y)"
}

function Start-SsmsWith {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [string]$Server = '(localdb)\MSSQLLocalDB',
        [string]$Database = 'test'
    )

    # SSMS 22 has no -E switch (its usage lists -S, -d, -U, -A, -C, -N); Windows
    # authentication is what you get when no -U is supplied.
    $exe = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe'
    Start-Process -FilePath $exe -ArgumentList @("`"$ScriptPath`"", '-S', $Server, '-d', $Database)
    Wait-Ssms | Out-Null
    Write-Host 'SSMS started.'
}

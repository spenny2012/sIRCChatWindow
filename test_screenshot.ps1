Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinAPI {
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string lclassName, string windowTitle);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$proc = Start-Process -FilePath "c:\Src\sIRCWpfChatWindow\IrcChatWpf\bin\x64\Debug\net8.0-windows\win-x64\IrcChatWpf.exe" -PassThru
$hwnd = [IntPtr]::Zero
for ($i=0; $i -lt 50 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 100
    $hwnd = [WinAPI]::FindWindow($null, "High-Performance IRC Chat")
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "Window not found"; $proc | Stop-Process -Force; exit 1 }
[WinAPI]::ShowWindow($hwnd, 1) | Out-Null
[WinAPI]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

$btn = [WinAPI]::FindWindowEx($hwnd, [IntPtr]::Zero, $null, "Start Spam")
if ($btn -ne [IntPtr]::Zero) {
    [WinAPI]::SendMessage($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}
Start-Sleep -Seconds 2

[WinAPI]::RECT $rc = New-Object WinAPI+RECT
[WinAPI]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
$w = $rc.Right - $rc.Left
$h = $rc.Bottom - $rc.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rc.Left, $rc.Top, 0, 0, New-Object System.Drawing.Size($w, $h))
$bmp.Save("c:\Src\sIRCWpfChatWindow\screenshot_test.png")
$g.Dispose()
$bmp.Dispose()
$proc | Stop-Process -Force

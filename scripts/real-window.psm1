# Drives a built iDevelop window on Windows through UI Automation patterns only, so it never moves the mouse.
# scripts/check-real-window.ps1 and the verify-idevelop skill share these commands.
using namespace System.Windows.Automation

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('IDevelopVerify.Win32' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace IDevelopVerify {
    public static class Win32 {
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    }
}
'@
}

$FakeAgent = Join-Path $PSScriptRoot '../tests/IDevelop.FakeAgent/bin/Release/net10.0/IDevelop.FakeAgent.exe'
$Fixtures = Join-Path $PSScriptRoot '../tests/IDevelop.Core.Tests/Fixtures'
$SettingsFile = Join-Path $env:APPDATA 'iDevelop\settings.json'
# An empty backup records that no preference existed. The owner file names the run that holds the backup.
$BackupFile = "$SettingsFile.verify-backup"
$OwnerFile = "$SettingsFile.verify-owner"

function Wait-Until([scriptblock] $Probe, [int] $Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $Probe
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-MainWindow([System.Diagnostics.Process] $Process) {
    Wait-Until {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            [AutomationElement]::FromHandle($Process.MainWindowHandle)
        }
    } 30
}

function Find-ById($Root, [string] $Id, [int] $Seconds = 10) {
    $condition = [PropertyCondition]::new([AutomationElement]::AutomationIdProperty, $Id)
    Wait-Until { $Root.FindFirst([TreeScope]::Descendants, $condition) } $Seconds
}

# The sidebar repeats each task title, so a title found inside the excluded element does not prove the card shows it.
function Find-NameOutside($Root, [string] $Name, [string] $ExcludedId) {
    $condition = [PropertyCondition]::new([AutomationElement]::NameProperty, $Name)
    $walker = [TreeWalker]::RawViewWalker
    Wait-Until {
        foreach ($match in $Root.FindAll([TreeScope]::Descendants, $condition)) {
            $inside = $false
            $parent = $walker.GetParent($match)
            while ($null -ne $parent -and -not $inside) {
                $inside = $parent.Current.AutomationId -eq $ExcludedId
                $parent = $walker.GetParent($parent)
            }
            if (-not $inside) { return $match }
        }
    } 10
}

# A dialog opens in a window of its own, so it is searched across the process's top-level windows.
function Find-InProcessWindows([System.Diagnostics.Process] $Process, [string] $Id, [int] $Seconds = 10) {
    $processCondition = [PropertyCondition]::new([AutomationElement]::ProcessIdProperty, $Process.Id)
    $idCondition = [PropertyCondition]::new([AutomationElement]::AutomationIdProperty, $Id)
    Wait-Until {
        foreach ($top in [AutomationElement]::RootElement.FindAll([TreeScope]::Children, $processCondition)) {
            $found = $top.FindFirst([TreeScope]::Descendants, $idCondition)
            if ($found) { return $found }
        }
    } $Seconds
}

# A picker's list opens in a window of its own, so its entries are searched across the process's windows.
function Find-AllInProcess([System.Diagnostics.Process] $Process, $ControlType) {
    $condition = [AndCondition]::new(
        [PropertyCondition]::new([AutomationElement]::ProcessIdProperty, $Process.Id),
        [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, $ControlType))
    [AutomationElement]::RootElement.FindAll([TreeScope]::Descendants, $condition)
}

function Get-Value($Element) {
    $Element.GetCurrentPattern([ValuePattern]::Pattern).Current.Value
}

function Invoke-Element($Element) {
    $Element.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
}

function Select-Element($Element) {
    $Element.GetCurrentPattern([SelectionItemPattern]::Pattern).Select()
}

function Test-Selected($Element) {
    $Element.GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected
}

function Set-Text($Element, [string] $Text) {
    $Element.GetCurrentPattern([ValuePattern]::Pattern).SetValue($Text)
}

function Save-Screenshot($Window, [string] $Path) {
    $hwnd = [IntPtr] $Window.Current.NativeWindowHandle
    $rect = [IDevelopVerify.Win32+RECT]::new()
    [IDevelopVerify.Win32]::GetWindowRect($hwnd, [ref] $rect) | Out-Null
    $bitmap = [System.Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [IDevelopVerify.Win32]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $graphics.ReleaseHdc($hdc)
    $bitmap.Save($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

function Close-Window($Window) {
    $Window.GetCurrentPattern([WindowPattern]::Pattern).Close()
}

function Get-SettingsPath { $SettingsFile }

function Get-SettingsText {
    if ([IO.File]::Exists($SettingsFile)) { [IO.File]::ReadAllText($SettingsFile) }
}

function Get-SettingsTheme {
    $text = Get-SettingsText
    if ($text) { ($text | ConvertFrom-Json).theme }
}

function Get-SettingsOwner {
    if ([IO.File]::Exists($OwnerFile)) { [IO.File]::ReadAllText($OwnerFile) | ConvertFrom-Json }
}

# The owner process's life bounds the session. The calling shell runs no other session, so a backup it owns is a leftover.
function Test-OwnerAlive($Owner) {
    if ($Owner.pid -eq $PID) { return $false }
    $process = Get-Process -Id $Owner.pid -ErrorAction SilentlyContinue
    $null -ne $process -and $process.StartTime.ToUniversalTime().Ticks -eq $Owner.started
}

function Write-SettingsOwner([string] $Run, [System.Diagnostics.Process] $Process) {
    $owner = [ordered]@{ run = $Run; pid = $Process.Id; started = $Process.StartTime.ToUniversalTime().Ticks }
    [IO.File]::WriteAllText($OwnerFile, ($owner | ConvertTo-Json -Compress))
}

function Restore-Backup {
    if ([IO.File]::Exists($BackupFile)) {
        if ((Get-Item -LiteralPath $BackupFile).Length -eq 0) {
            [IO.File]::Delete($SettingsFile)
        } else {
            [IO.File]::Copy($BackupFile, $SettingsFile, $true)
        }
        [IO.File]::Delete($BackupFile)
    }
    if ([IO.File]::Exists($OwnerFile)) { [IO.File]::Delete($OwnerFile) }
}

# The preference is per user, so one verification run at a time may replace it. A backup left by a killed run is
# restored before a new one is taken.
function Backup-Settings([string] $Run, [System.Diagnostics.Process] $Owner = (Get-Process -Id $PID)) {
    $current = Get-SettingsOwner
    if ($current -and $current.run -ne $Run -and (Test-OwnerAlive $current)) {
        throw "Another verification run owns the iDevelop theme preference: $($current.run), pid $($current.pid). Only one may run at a time per Windows account. If it is a verify-idevelop session, Stop-IDevelop -Run '$($current.run)' ends it."
    }
    if (-not ($current -and $current.run -eq $Run -and [IO.File]::Exists($BackupFile))) {
        Restore-Backup
        [IO.Directory]::CreateDirectory((Split-Path -LiteralPath $SettingsFile)) | Out-Null
        if ([IO.File]::Exists($SettingsFile)) { [IO.File]::Move($SettingsFile, $BackupFile) } else { [IO.File]::WriteAllText($BackupFile, '') }
    }
    Write-SettingsOwner $Run $Owner
}

function Restore-Settings([string] $Run) {
    if ((Get-SettingsOwner).run -eq $Run) { Restore-Backup }
}

# A fake Codex for a PATH of its own, so a run needs no agent account. It answers the probes, starts a process whose
# parent exits, waits until the gate file exists, and then replays a successful turn. Returns the program it runs.
function New-FakeCodex([string] $Bin, [string] $Gate, [string] $SleeperPidFile) {
    [IO.Directory]::CreateDirectory($Bin) | Out-Null
    $rules = [ordered]@{ rules = @(
        [ordered]@{ when = @('debug', 'models'); steps = @(@{ replay = (Join-Path $Fixtures 'codex-debug-models.json') }) },
        [ordered]@{ when = @('login', 'status'); steps = @(@{ print = 'Logged in using ChatGPT' }) },
        [ordered]@{ when = @('exec', '--json'); steps = @(@{ spawnThroughCmd = $SleeperPidFile }, @{ waitForFile = $Gate }, @{ replay = (Join-Path $Fixtures 'codex-success.jsonl') }) }
    ) }
    [IO.File]::WriteAllText((Join-Path $Bin 'codex.rules.json'), ($rules | ConvertTo-Json -Depth 6 -Compress))
    [IO.File]::WriteAllText((Join-Path $Bin 'codex.cmd'), "@`"$FakeAgent`" --rules `"$(Join-Path $Bin 'codex.rules.json')`" -- %*`r`n")
    $FakeAgent
}

Export-ModuleMember -Function Wait-Until, Find-MainWindow, Find-ById, Find-NameOutside, Find-InProcessWindows, Find-AllInProcess,
    Get-Value, Invoke-Element, Select-Element, Test-Selected, Set-Text, Save-Screenshot, Close-Window,
    Get-SettingsPath, Get-SettingsText, Get-SettingsTheme, Backup-Settings, Restore-Settings, New-FakeCodex

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
function New-FakeCodex([string] $Bin, [string] $Gate, [string] $SleeperPidFile, [switch] $BlockOtherClients, [string] $Agent = $FakeAgent) {
    [IO.Directory]::CreateDirectory($Bin) | Out-Null
    $rules = [ordered]@{ rules = @(
        [ordered]@{ when = @('debug', 'models'); steps = @(@{ replay = (Join-Path $Fixtures 'codex-debug-models.json') }) },
        [ordered]@{ when = @('login', 'status'); steps = @(@{ print = 'Logged in using ChatGPT' }) },
        [ordered]@{ when = @('exec', '--json'); steps = @(@{ spawnThroughCmd = $SleeperPidFile }, @{ waitForFile = $Gate }, @{ replay = (Join-Path $Fixtures 'codex-success.jsonl') }) }
    ) }
    [IO.File]::WriteAllText((Join-Path $Bin 'codex.rules.json'), ($rules | ConvertTo-Json -Depth 6 -Compress))
    [IO.File]::WriteAllText((Join-Path $Bin 'codex.cmd'), "@`"$Agent`" --rules `"$(Join-Path $Bin 'codex.rules.json')`" -- %*`r`n")
    if ($BlockOtherClients) {
        # iDevelop also searches the user's PATH, where the real clients would spend quota. A shim earlier on the search
        # path that answers no probe leaves its client not ready, so it cannot run.
        [IO.File]::WriteAllText((Join-Path $Bin 'none.rules.json'), '{"rules":[]}')
        foreach ($client in 'claude', 'pi', 'agy') {
            [IO.File]::WriteAllText((Join-Path $Bin "$client.cmd"), "@`"$Agent`" --rules `"$(Join-Path $Bin 'none.rules.json')`" -- %*`r`n")
        }
    }
    $Agent
}

# A verify-idevelop session spans many shells. Its run folder under .verify holds session.json, which records the
# process it started, and every piece of evidence. Stop-IDevelop never deletes the folder.
$Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ReleaseExe = Join-Path $Repo 'src\IDevelop.Desktop\bin\Release\net10.0\IDevelop.Desktop.exe'
$VerifyRoot = Join-Path $Repo '.verify'
$AgentRows = 'AgentClaudeCode', 'AgentCodex', 'AgentPi', 'AgentAntigravity'

function Get-SessionState([string] $Run) {
    if (-not $Run) {
        $Run = Get-ChildItem -LiteralPath $VerifyRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
            Where-Object { [IO.File]::Exists((Join-Path $_.FullName 'session.json')) } | Select-Object -First 1 -ExpandProperty FullName
        if (-not $Run) { throw "No session in $VerifyRoot. Start-IDevelop starts one." }
    }
    $file = Join-Path $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Run) 'session.json'
    if (-not [IO.File]::Exists($file)) { throw "$Run holds no session.json." }
    [IO.File]::ReadAllText($file) | ConvertFrom-Json
}

function Save-SessionState($State) {
    [IO.File]::WriteAllText((Join-Path $State.run 'session.json'), ($State | ConvertTo-Json))
}

# The start time and the path keep a reused process id from passing as the session's window.
function Get-SessionProcess($State) {
    $process = Get-Process -Id $State.pid -ErrorAction SilentlyContinue
    if ($process -and $process.StartTime.ToUniversalTime().Ticks -eq $State.started -and $process.Path -eq $State.exe) { $process }
}

function Get-FakeProcesses([string] $Run) {
    $prefix = (Join-Path $Run 'fake-bin') + '\'
    Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }
}

function Add-Transcript([string] $Run, [string] $Line) {
    Add-Content -LiteralPath (Join-Path $Run 'transcript.txt') -Value $Line -Encoding utf8
}

function New-Session($State, $Process, $Window) {
    [pscustomobject]@{
        Run = $State.run
        Project = $State.project
        Gate = Join-Path $State.run 'fake-bin\gate'
        Process = $Process
        Window = $Window
    }
}

function Start-IDevelop([string] $Project, [ValidateNotNullOrEmpty()] [string] $Run, [switch] $Reopen, [switch] $Empty, [switch] $RealClients) {
    if (-not [IO.File]::Exists($ReleaseExe)) { throw "No Release build at $ReleaseExe. Run dotnet build -c Release first." }
    if ($Run -or $Reopen) {
        $previous = Get-SessionState $Run
        if ($previous.stopped) { throw "The session in $($previous.run) is stopped. Start-IDevelop starts a new one." }
        if (Get-SessionProcess $previous) { throw "The session in $($previous.run) still runs pid $($previous.pid). Connect-IDevelop drives it." }
        $Run = $previous.run
        if (-not $Project -and -not $Empty) { $Project = $previous.project }
    } else {
        $Run = Join-Path $VerifyRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
    }
    if ($Project) {
        $Project = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Project)
        if (-not [IO.Directory]::Exists($Project)) { throw "No project folder at $Project." }
    }
    $bin = Join-Path $Run 'fake-bin'
    # A copy of its own marks every fake process as this session's, so Stop-IDevelop can find each one it left.
    $agent = Join-Path $bin 'agent\IDevelop.FakeAgent.exe'
    if (-not $RealClients -and -not [IO.File]::Exists($agent) -and -not [IO.File]::Exists($FakeAgent)) {
        throw "No fake agent at $FakeAgent. Run dotnet build -c Release first."
    }

    [IO.Directory]::CreateDirectory($Run) | Out-Null
    if (-not $Project) {
        $Project = Join-Path $Run $(if ($Empty) { 'empty-project' } else { 'project' })
        if ($Empty) {
            [IO.Directory]::CreateDirectory($Project) | Out-Null
        } elseif (-not [IO.Directory]::Exists($Project)) {
            Copy-Item -Recurse -LiteralPath (Join-Path $Repo 'samples\storage-change') -Destination $Project
        }
    }
    if (-not $RealClients) {
        if (-not [IO.File]::Exists($agent)) {
            [IO.Directory]::CreateDirectory((Split-Path $agent)) | Out-Null
            Copy-Item -Path (Join-Path (Split-Path $FakeAgent) 'IDevelop.FakeAgent.*') -Destination (Split-Path $agent)
        }
        New-FakeCodex $bin (Join-Path $bin 'gate') (Join-Path $bin 'sleeper.pid') -BlockOtherClients -Agent $agent | Out-Null
    }

    # The preference moves aside only once nothing is left to refuse the start, and comes back if the launch fails.
    Backup-Settings $Run
    $savedPath = $env:PATH
    if (-not $RealClients) { $env:PATH = $bin }
    try {
        $process = Start-Process -FilePath $ReleaseExe -ArgumentList "`"$Project`"" -PassThru
    } catch {
        Restore-Settings $Run
        throw
    } finally {
        $env:PATH = $savedPath
    }
    Write-SettingsOwner $Run $process
    $state = [pscustomobject]@{
        run = $Run; project = $Project; exe = $ReleaseExe; pid = $process.Id
        started = $process.StartTime.ToUniversalTime().Ticks; fakeCodex = -not $RealClients; stopped = $false
    }
    Save-SessionState $state
    Add-Transcript $Run "INFO started pid $($process.Id) on $Project with $(if ($RealClients) { 'the real clients' } else { 'the fake Codex' })"
    $window = Find-MainWindow $process
    if (-not $window) { throw "No window appeared for pid $($process.Id). Stop-IDevelop -Run '$Run' cleans up." }
    New-Session $state $process $window
}

function Connect-IDevelop([string] $Run) {
    $state = Get-SessionState $Run
    if ($state.stopped) { throw "The session in $($state.run) is stopped. Start-IDevelop starts a new one." }
    $process = Get-SessionProcess $state
    if (-not $process) { throw "The window of the session in $($state.run) is not running. Start-IDevelop -Run '$($state.run)' opens it again, and Stop-IDevelop cleans up." }
    $window = Find-MainWindow $process
    if (-not $window) { throw "Pid $($process.Id) shows no window. Stop-IDevelop -Run '$($state.run)' cleans up." }
    New-Session $state $process $window
}

function Get-SidebarTasks($Window) {
    (Find-ById $Window 'SidebarTasks').FindAll([TreeScope]::Children, [Condition]::TrueCondition)
}

# An entry chosen in the open list counts as the user's choice. An entry's name is its label, then " · " and a note.
function Select-PickerEntry($Session, [string] $Id, [string] $Entry) {
    $expand = (Find-ById $Session.Window $Id).GetCurrentPattern([ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $item = Wait-Until {
        Find-AllInProcess $Session.Process ([ControlType]::ListItem) |
            Where-Object { $_.Current.Name -eq $Entry -or $_.Current.Name.StartsWith("$Entry $([char] 0xB7) ") } | Select-Object -First 1
    } 5
    if ($item) { Select-Element $item }
    try { $expand.Collapse() } catch {}
    if (-not $item) { throw "The $Id picker offers no entry '$Entry'." }
}

function Assert-Step($Session, [bool] $Ok, [string] $What) {
    $line = '{0} {1}' -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $What
    Add-Transcript $Session.Run $line
    $line
    if (-not $Ok) { throw "Check failed: $What" }
}

# The screenshot shows what the user sees, and the copies keep what the app wrote at that moment. A closed window
# leaves only the copies.
function Save-Evidence($Session, [string] $Name) {
    $shot = ' The window is closed, so no screenshot.'
    if (-not $Session.Process.HasExited) {
        Save-Screenshot $Session.Window (Join-Path $Session.Run "$Name.png")
        $shot = " Saved $Name.png."
    }
    $idp = Get-Item -LiteralPath (Join-Path $Session.Project '.idp') -ErrorAction SilentlyContinue
    $copied = 0
    foreach ($file in $(if ($idp) { Get-ChildItem -LiteralPath $idp.FullName -Recurse -File })) {
        if ($file.Name -eq 'events.jsonl' -or ($file.Extension -eq '.json' -and $file.Directory.Name -eq 'workflows')) {
            $target = Join-Path (Join-Path $Session.Run $Name) ($file.FullName.Substring($idp.FullName.Length + 1))
            [IO.Directory]::CreateDirectory((Split-Path -LiteralPath $target)) | Out-Null
            [IO.File]::Copy($file.FullName, $target, $true)
            $copied++
        }
    }
    "Copied $copied project files to $(Join-Path $Session.Run $Name).$shot"
}

function Format-Doctor([bool] $Ok, [string] $Line) {
    '{0} {1}' -f $(if ($Ok) { 'OK ' } else { 'BAD' }), $Line
}

# Read-only. It reads the session, the process, the build's age, the window, and the preference backup.
function Test-IDevelop([string] $Run) {
    $state = Get-SessionState $Run
    $lines = & {
        "INFO session $($state.run)$(if ($state.stopped) { ', stopped' })"
        $process = Get-SessionProcess $state
        Format-Doctor ($null -ne $process) "process $($state.pid) is alive"
        if ($process) {
            Format-Doctor ($process.Path -eq $ReleaseExe) "it runs $($process.Path), and this checkout's build is $ReleaseExe"
        }
        # A change to one project rebuilds only its own assembly, and the exe changes only with the desktop project.
        foreach ($project in Get-ChildItem -LiteralPath (Join-Path $Repo 'src') -Directory) {
            $dll = Get-Item -LiteralPath (Join-Path (Split-Path $ReleaseExe) "$($project.Name).dll") -ErrorAction SilentlyContinue
            $newest = Get-ChildItem -LiteralPath $project.FullName -Recurse -File | Where-Object FullName -notmatch '\\(bin|obj)\\' |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            Format-Doctor ($dll -and $dll.LastWriteTime -gt $newest.LastWriteTime) "$($project.Name).dll ($($dll.LastWriteTime)) is newer than every file under src\$($project.Name), the newest being $($newest.Name) ($($newest.LastWriteTime))"
        }
        if ($process) {
            $window = Find-MainWindow $process
            Format-Doctor ($null -ne $window) "window title '$($window.Current.Name)'"
            if ($window) {
                foreach ($id in $AgentRows) {
                    $row = Find-ById $window $id 2
                    "INFO $id '$($row.Current.Name)' $($row.Current.HelpText)"
                }
            }
        }
        "INFO clients: $(if ($state.fakeCodex) { "the fake Codex, which finishes once $(Join-Path $state.run 'fake-bin\gate') exists, with Claude Code, Pi, and Antigravity CLI blocked" } else { 'the real clients' })"
        $fakes = @(Get-FakeProcesses $state.run)
        "INFO the session's fake agent processes: $(if ($fakes) { $fakes.Id -join ', ' } else { 'none' })"
        $owner = Get-SettingsOwner
        if (-not [IO.File]::Exists($BackupFile)) {
            Format-Doctor ($state.stopped) $(if ($state.stopped) { 'no theme preference backup is left' } else { 'this live session holds no theme preference backup' })
        } elseif ($owner.run -eq $state.run) {
            Format-Doctor (-not $state.stopped) $(if ($state.stopped) { 'this stopped session still holds the theme preference backup. Stop-IDevelop restores it' } else { 'this session holds the theme preference backup' })
        } elseif ($owner -and (Test-OwnerAlive $owner)) {
            Format-Doctor $false "another live run holds the theme preference backup: $($owner.run), pid $($owner.pid)"
        } else {
            "WARN a theme preference backup from a killed run is left$(if ($owner) { " by $($owner.run)" }). The next Start-IDevelop or check-real-window.ps1 restores it."
        }
    }
    $lines
    $bad = @($lines | Where-Object { $_ -like 'BAD *' }).Count
    if ($bad) { "Doctor: not worth driving ($bad BAD)" } else { 'Doctor: worth driving' }
}

function Stop-IDevelop([string] $Run) {
    $state = Get-SessionState $Run
    $done = 'the window had already closed'
    try {
        $process = Get-SessionProcess $state
        if ($process) {
            # A run in progress asks first, and unsaved edits ask next. The answers stop the run and keep the last save.
            try {
                $window = Find-MainWindow $process
                if ($window) { Close-Window $window }
                $deadline = (Get-Date).AddSeconds(15)
                while ($window -and -not $process.HasExited -and (Get-Date) -lt $deadline) {
                    foreach ($id in 'StopAndLeave', 'DiscardChanges') {
                        $button = Find-InProcessWindows $process $id 1
                        if ($button) { Invoke-Element $button }
                    }
                }
            } catch {}
            if ($process.HasExited) {
                $done = 'closed the window'
            } else {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
                $done = "killed pid $($process.Id)"
            }
        }
        # A run that ends on its own leaves the fake Codex's sleeper running, as a real client's dev server would.
        $fakes = @(Get-FakeProcesses $state.run)
        foreach ($fake in $fakes) { $fake.Kill() }
        if ($fakes) { $done += ", stopped the session's fake agent processes $($fakes.Id -join ', ')" }
    } finally {
        $held = (Get-SettingsOwner).run -eq $state.run -and [IO.File]::Exists($BackupFile)
        Restore-Settings $state.run
        $done += $(if ($held) { ', and restored the theme preference' } else { ', and held no theme preference backup to restore' })
        $state.stopped = $true
        Save-SessionState $state
        Add-Transcript $state.run "INFO stopped: $done"
    }
    "Stopped: $done. The evidence stays in $($state.run)."
    if ([IO.File]::Exists($BackupFile)) {
        $owner = Get-SettingsOwner
        if ($owner -and (Test-OwnerAlive $owner)) {
            "WARN another live run holds the theme preference backup: $($owner.run), pid $($owner.pid). It restores the preference when it stops."
        } else {
            "WARN a theme preference backup from a killed run is left$(if ($owner) { " by $($owner.run)" }). The next Start-IDevelop or check-real-window.ps1 restores it."
        }
    }
}

Export-ModuleMember -Function Wait-Until, Find-MainWindow, Find-ById, Find-NameOutside, Find-InProcessWindows, Find-AllInProcess,
    Get-Value, Invoke-Element, Select-Element, Test-Selected, Set-Text, Save-Screenshot, Close-Window,
    Get-SettingsPath, Get-SettingsText, Get-SettingsTheme, Backup-Settings, Restore-Settings, New-FakeCodex,
    Start-IDevelop, Connect-IDevelop, Test-IDevelop, Get-SidebarTasks, Select-PickerEntry, Assert-Step, Save-Evidence, Stop-IDevelop

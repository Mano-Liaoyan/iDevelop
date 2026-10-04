# Drives a built iDevelop window on Windows through UI Automation patterns only, so it never moves the mouse.
# It replaces the user's theme preference while it runs and restores it afterward.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$results = [System.Collections.Generic.List[string]]::new()
function Check([bool] $ok, [string] $what) {
    $results.Add(("{0} {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what))
    if (-not $ok) { $results | ForEach-Object { Write-Output $_ }; throw "Check failed: $what" }
}

function Wait-Until([scriptblock] $Probe, [int] $seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $Probe
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-MainWindow([System.Diagnostics.Process] $process) {
    Wait-Until {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        }
    } 30
}

function Find-ById($root, [string] $id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    Wait-Until { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) } 10
}

# The sidebar repeats each task title, so a title found inside the excluded element does not prove the card shows it.
function Find-NameOutside($root, [string] $name, [string] $excludedId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    Wait-Until {
        foreach ($match in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
            $inside = $false
            $parent = $walker.GetParent($match)
            while ($null -ne $parent -and -not $inside) {
                $inside = $parent.Current.AutomationId -eq $excludedId
                $parent = $walker.GetParent($parent)
            }
            if (-not $inside) { return $match }
        }
    } 10
}

function Find-InProcessWindows([System.Diagnostics.Process] $process, [string] $id) {
    $processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $idCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    Wait-Until {
        foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)) {
            $found = $top.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCondition)
            if ($found) { return $found }
        }
    } 10
}

# A picker's list opens in a window of its own, so its entries are searched across the process's windows.
function Find-AllInProcess([System.Diagnostics.Process] $process, $controlType) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType))
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Value($element) {
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Invoke-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Select-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Is-Selected($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
}

function Set-Text($element, [string] $text) {
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}

function Save-Screenshot($window, [string] $name) {
    $hwnd = [IntPtr] $window.Current.NativeWindowHandle
    $rect = [Win32+RECT]::new()
    [Win32]::GetWindowRect($hwnd, [ref] $rect) | Out-Null
    $bitmap = [System.Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [Win32]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $graphics.ReleaseHdc($hdc)
    $bitmap.Save((Join-Path $run $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

function Close-Window($window) {
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
}

function With-App([string] $project, [scriptblock] $Body) {
    $process = Start-Process -FilePath $Exe -ArgumentList "`"$project`"" -PassThru
    try {
        $window = Find-MainWindow $process
        Check ($null -ne $window) 'main window appeared'
        & $Body $process $window
        Check ($process.WaitForExit(15000)) 'the app exited after its window closed'
    } finally {
        if (-not $process.HasExited) { $process.Kill() }
    }
}

$run = Join-Path $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir) (Get-Date -Format 'yyyyMMdd-HHmmss')
[IO.Directory]::CreateDirectory($run) | Out-Null
$settings = Join-Path $env:APPDATA 'iDevelop\settings.json'
# An empty backup records that no preference existed. A backup left by a killed run is restored before anything else.
$backup = "$settings.check-real-window-backup"

function Restore-Settings {
    if (-not [IO.File]::Exists($backup)) { return }
    if ((Get-Item -LiteralPath $backup).Length -eq 0) {
        [IO.File]::Delete($settings)
    } else {
        [IO.File]::Copy($backup, $settings, $true)
    }
    [IO.File]::Delete($backup)
}

function Settings-Text {
    if ([IO.File]::Exists($settings)) { [IO.File]::ReadAllText($settings) }
}

function Settings-Theme {
    $text = Settings-Text
    if ($text) { ($text | ConvertFrom-Json).theme }
}

Restore-Settings
[IO.Directory]::CreateDirectory((Split-Path -LiteralPath $settings)) | Out-Null
if ([IO.File]::Exists($settings)) { [IO.File]::Move($settings, $backup) } else { [IO.File]::WriteAllText($backup, '') }

try {
    $project = Join-Path $run 'edit-project'
    [IO.Directory]::CreateDirectory($project) | Out-Null
    $title = 'Plan the release 发布计划 and coordinate the reviewers across three teams'

    With-App $project {
        param($process, $window)
        Check ($window.Current.Name -eq 'edit-project - iDevelop') "title before edits is '$($window.Current.Name)'"
        Check ($null -eq (Settings-Theme)) 'a first launch writes no theme preference'

        Invoke-Element (Find-ById $window 'AddTask')
        Invoke-Element (Find-ById $window 'AddTask')
        $titleBox = Find-ById $window 'TaskTitle'
        Check ($null -ne $titleBox) 'inspector title box appeared for the new task'
        Set-Text $titleBox $title
        Set-Text (Find-ById $window 'TaskInstructions') "First line`nSecond line"
        Check ((Wait-Until { $window.Current.Name -eq 'edit-project* - iDevelop' }) -eq $true) "title shows unsaved changes: '$($window.Current.Name)'"
        Check ($null -ne (Find-NameOutside $window $title 'SidebarTasks')) 'the card shows the typed title'
        Save-Screenshot $window 'before-save.png'

        Invoke-Element (Find-ById $window 'Save')
        Check ((Wait-Until { $window.Current.Name -eq 'edit-project - iDevelop' }) -eq $true) "title clears after save: '$($window.Current.Name)'"
        $files = @(Get-ChildItem (Join-Path $project '.idp/workflows') -Filter *.json)
        Check ($files.Count -eq 1) "one workflow file saved (found $($files.Count))"
        $json = Get-Content -Raw -Encoding UTF8 $files[0].FullName | ConvertFrom-Json
        Check ($json.tasks.Count -eq 2) "saved file has two tasks (found $($json.tasks.Count))"
        Check (@($json.tasks | Where-Object { $_.title -eq $title }).Count -eq 1) 'saved file has the typed title'

        Set-Text (Find-ById $window 'TaskTitle') 'Unsaved edit'
        Close-Window $window
        $cancel = Find-InProcessWindows $process 'CancelChanges'
        Check ($null -ne $cancel) 'closing with unsaved changes shows the save prompt'
        Invoke-Element $cancel
        Start-Sleep -Milliseconds 500
        Check (-not $process.HasExited) 'Cancel keeps the app open'
        Check ($window.Current.Name -eq 'edit-project* - iDevelop') "title still shows unsaved changes after Cancel: '$($window.Current.Name)'"

        Close-Window $window
        $discard = Find-InProcessWindows $process 'DiscardChanges'
        Check ($null -ne $discard) 'the prompt appears again on the second close'
        Invoke-Element $discard
    }

    With-App $project {
        param($process, $window)
        Check ($window.Current.Name -eq 'edit-project - iDevelop') "reopened title is '$($window.Current.Name)'"
        Check ($null -ne (Find-NameOutside $window $title 'SidebarTasks')) 'reopened canvas shows the saved task'
        $discarded = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Unsaved edit'))
        Check ($null -eq $discarded) 'the discarded edit is not on the reopened canvas'
        Close-Window $window
    }

    $project = Join-Path $run 'agents-project'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '../samples/storage-change') -Destination $project
    $agentRows = 'AgentClaudeCode', 'AgentCodex', 'AgentPi', 'AgentAntigravity'

    With-App $project {
        param($process, $window)
        foreach ($id in $agentRows) { Check ($null -ne (Find-ById $window $id)) "the AGENTS section lists $id" }
        $settled = Wait-Until { -not (@($agentRows | ForEach-Object { (Find-ById $window $_).Current.Name }) -match 'Checking') } 120
        Check ($settled -eq $true) 'every agent client finished its check within 120 seconds'
        foreach ($id in $agentRows) { $row = Find-ById $window $id; $results.Add("INFO $id says '$($row.Current.Name)'. $($row.Current.HelpText)") }
        Save-Screenshot $window 'agents.png'

        # The sample's first task asks for Claude Code with Claude Opus 5.5 at high. The model's name comes from the
        # catalog, or its id when this machine has no Claude Code.
        $sidebarRows = (Find-ById $window 'SidebarTasks').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        Select-Element $sidebarRows[0]
        $client = Find-ById $window 'TaskClient'
        Check ((Value $client) -like 'Claude Code*') "the client picker shows Claude Code (found '$(Value $client)')"
        Check ((Value (Find-ById $window 'TaskModel')) -match '^(Claude Opus 5\.5|claude-opus-5-5 .+)$') "the model picker shows Claude Opus 5.5 (found '$(Value (Find-ById $window 'TaskModel'))')"
        Check ((Value (Find-ById $window 'TaskReasoning')) -eq 'high') "the reasoning picker shows high (found '$(Value (Find-ById $window 'TaskReasoning'))')"

        # The second task asks for Codex with GPT-6-Sol at medium, which is not Codex's first model.
        Select-Element $sidebarRows[1]
        Check ((Wait-Until { (Value $client) -like 'Codex*' }) -eq $true) "the client picker follows the second task (found '$(Value $client)')"
        Check ((Value (Find-ById $window 'TaskModel')) -match '^(GPT-6-Sol|gpt-6-sol .+)$') "choosing another task keeps its model (found '$(Value (Find-ById $window 'TaskModel'))')"
        Check ((Value (Find-ById $window 'TaskReasoning')) -eq 'medium') "choosing another task keeps its reasoning (found '$(Value (Find-ById $window 'TaskReasoning'))')"
        Select-Element $sidebarRows[0]
        Check ((Wait-Until { (Value $client) -like 'Claude Code*' }) -eq $true) "the client picker follows the first task again (found '$(Value $client)')"
        Check ($window.Current.Name -eq 'agents-project - iDevelop') "choosing tasks in the sidebar edits nothing: '$($window.Current.Name)'"

        $client.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $entries = Wait-Until { @(Find-AllInProcess $process ([System.Windows.Automation.ControlType]::ListItem) | Where-Object { $_.Current.Name -match '^(None|Claude Code|Codex|Pi|Antigravity CLI)( · .+)?$' }) | Where-Object { $_ } } 10
        Check (@($entries).Count -eq 5) "the client picker offers None and the four clients (found $(@($entries).Count))"
        Select-Element (@($entries) | Where-Object { $_.Current.Name -like 'Codex*' })
        try { $client.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
        Check ((Wait-Until { (Value $client) -like 'Codex*' }) -eq $true) "choosing Codex selects it (found '$(Value $client)')"
        Check ((Wait-Until { (Find-ById $window 'PermissionNote').Current.Name -like 'Codex may edit*' }) -eq $true) 'the permission note follows the chosen client'
        Check ((Wait-Until { $window.Current.Name -eq 'agents-project* - iDevelop' }) -eq $true) "choosing an agent is an unsaved edit: '$($window.Current.Name)'"
        Save-Screenshot $window 'pickers.png'
        Close-Window $window
        Invoke-Element (Find-InProcessWindows $process 'DiscardChanges')
    }

    # A fake Codex on an otherwise empty PATH, so a run needs no agent account. It answers the probes, starts a process
    # that outlives it, and waits at a gate that the check never opens.
    $project = Join-Path $run 'run-project'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '../samples/storage-change') -Destination $project
    $fakeBin = Join-Path $run 'fake-bin'
    [IO.Directory]::CreateDirectory($fakeBin) | Out-Null
    $fakeAgent = Join-Path $PSScriptRoot '../tests/IDevelop.FakeAgent/bin/Release/net10.0/IDevelop.FakeAgent.exe'
    Check ([IO.File]::Exists($fakeAgent)) "the fake agent is built at $fakeAgent"
    $sleeperPid = Join-Path $run 'sleeper.pid'
    $rules = [ordered]@{ rules = @(
        [ordered]@{ when = @('debug', 'models'); steps = @(@{ replay = (Join-Path $PSScriptRoot '../tests/IDevelop.Core.Tests/Fixtures/codex-debug-models.json') }) },
        [ordered]@{ when = @('login', 'status'); steps = @(@{ print = 'Logged in using ChatGPT' }) },
        [ordered]@{ when = @('exec', '--json'); steps = @(@{ spawnThroughCmd = $sleeperPid }, @{ waitForFile = (Join-Path $run 'never') }) }
    ) }
    [IO.File]::WriteAllText((Join-Path $fakeBin 'codex.rules.json'), ($rules | ConvertTo-Json -Depth 6 -Compress))
    [IO.File]::WriteAllText((Join-Path $fakeBin 'codex.cmd'), "@`"$fakeAgent`" --rules `"$(Join-Path $fakeBin 'codex.rules.json')`" -- %*`r`n")
    $savedPath = $env:PATH
    $env:PATH = $fakeBin
    try {
        With-App $project {
            param($process, $window)
            Check ((Wait-Until { (Find-ById $window 'AgentCodex').Current.Name -like 'Ready*' } 60) -eq $true) 'the fake Codex is ready'
            Select-Element ((Find-ById $window 'SidebarTasks').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)[1])
            Invoke-Element (Find-ById $window 'RunTask')
            $bar = Find-ById $window 'RunBar'
            Check ($null -ne $bar) 'UI Automation finds the run bar'
            Check ((Find-ById $window 'RunBarTask').Current.Name -eq 'Implement atomic save') "the run bar names the running task (found '$((Find-ById $window 'RunBarTask').Current.Name)')"
            $sleeper = Wait-Until { if ([IO.File]::Exists($sleeperPid)) { [int]([IO.File]::ReadAllText($sleeperPid)) } } 30
            Check ($null -ne $sleeper) 'the fake Codex started a process whose parent exits'
            Save-Screenshot $window 'run-bar.png'

            Invoke-Element (Find-ById $window 'RunBarCancel')
            Check ((Wait-Until { (Find-ById $window 'LastRunStatus').Current.Name -eq 'Cancelled' } 30) -eq $true) 'Cancel records the run as cancelled'
            Check ((Wait-Until { $null -eq (Get-Process -Id $sleeper -ErrorAction SilentlyContinue) } 30) -eq $true) "Cancel stops the process whose parent had exited (pid $sleeper)"
            Check ($window.Current.Name -eq 'run-project - iDevelop') "choosing and running a task edits nothing: '$($window.Current.Name)'"
            Close-Window $window
        }
    } finally {
        $env:PATH = $savedPath
    }

    $project = Join-Path $run 'theme-project'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '../samples/storage-change') -Destination $project

    With-App $project {
        param($process, $window)
        Check ($null -eq (Settings-Text)) 'the earlier launches left no theme preference'
        Check (Is-Selected (Find-ById $window 'ThemeSystem')) 'without a preference the System segment is chosen'
        Select-Element (Find-ById $window 'ThemeLight')
        Check ((Wait-Until { (Settings-Theme) -eq 'light' }) -eq $true) "choosing Light saves 'light' (found '$(Settings-Theme)')"
        Invoke-Element (Find-ById $window 'FitToScreen')
        Start-Sleep -Milliseconds 500
        Save-Screenshot $window 'light.png'

        Select-Element (Find-ById $window 'ThemeDark')
        Check ((Wait-Until { (Settings-Theme) -eq 'dark' }) -eq $true) "choosing Dark saves 'dark' (found '$(Settings-Theme)')"
        Check (-not (Is-Selected (Find-ById $window 'ThemeLight'))) 'the Light segment is no longer chosen'
        Start-Sleep -Milliseconds 500
        Save-Screenshot $window 'dark.png'
        Close-Window $window
    }

    # The app writes this compactly, so a launch that rewrote the file would change these bytes.
    $handWritten = '{ "theme": "dark" }'
    [IO.File]::WriteAllText($settings, $handWritten)
    With-App $project {
        param($process, $window)
        Check (Is-Selected (Find-ById $window 'ThemeDark')) 'Dark is still chosen after a restart'
        Check ((Settings-Text) -eq $handWritten) "a launch leaves the preference file byte for byte (found '$(Settings-Text)')"
        Select-Element (Find-ById $window 'ThemeSystem')
        Check ((Wait-Until { (Settings-Theme) -eq 'system' }) -eq $true) "choosing System saves 'system' (found '$(Settings-Theme)')"
        Close-Window $window
    }
} finally {
    Restore-Settings
}

$results | ForEach-Object { Write-Output $_ }

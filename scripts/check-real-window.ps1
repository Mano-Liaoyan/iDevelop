# Drives a built iDevelop window on Windows through UI Automation patterns only, so it never moves the mouse.
# It replaces the user's theme preference while it runs and restores it afterward.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'real-window.psm1') -Force

$results = [System.Collections.Generic.List[string]]::new()
function Check([bool] $ok, [string] $what) {
    $results.Add(("{0} {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what))
    if (-not $ok) { $results | ForEach-Object { Write-Output $_ }; throw "Check failed: $what" }
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
Backup-Settings $run

try {
    $project = Join-Path $run 'edit-project'
    [IO.Directory]::CreateDirectory($project) | Out-Null
    $title = 'Plan the release 发布计划 and coordinate the reviewers across three teams'

    With-App $project {
        param($process, $window)
        Check ($window.Current.Name -eq 'edit-project - iDevelop') "title before edits is '$($window.Current.Name)'"
        Check ($null -eq (Get-SettingsTheme)) 'a first launch writes no theme preference'

        foreach ($add in 1..2) {
            Invoke-Element (Find-ById $window 'AddTask')
            $implement = Find-ById $window 'AddNodeItem'
            Check ($implement.Current.Name -eq 'Add Implement') "the Add popover lists Implement first: '$($implement.Current.Name)'"
            Invoke-Element $implement
        }
        $titleBox = Find-ById $window 'TaskTitle'
        Check ($null -ne $titleBox) 'inspector title box appeared for the new task'
        Set-Text $titleBox $title
        Set-Text (Find-ById $window 'TaskInstructions') "First line`nSecond line"
        Check ((Wait-Until { $window.Current.Name -eq 'edit-project* - iDevelop' }) -eq $true) "title shows unsaved changes: '$($window.Current.Name)'"
        Check ($null -ne (Find-NameOutside $window $title 'SidebarTasks')) 'the card shows the typed title'
        Save-Screenshot $window (Join-Path $run 'before-save.png')

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
        Save-Screenshot $window (Join-Path $run 'agents.png')

        # The sample's first task asks for Claude Code with Claude Opus 5.5 at high. The model's name comes from the
        # catalog, or its id when this machine has no Claude Code.
        $sidebarRows = (Find-ById $window 'SidebarTasks').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        Select-Element $sidebarRows[0]
        $client = Find-ById $window 'TaskClient'
        Check ((Get-Value $client) -like 'Claude Code*') "the client picker shows Claude Code (found '$(Get-Value $client)')"
        Check ((Get-Value (Find-ById $window 'TaskModel')) -match '^(Claude Opus 5\.5|claude-opus-5-5 .+)$') "the model picker shows Claude Opus 5.5 (found '$(Get-Value (Find-ById $window 'TaskModel'))')"
        Check ((Get-Value (Find-ById $window 'TaskReasoning')) -eq 'high') "the reasoning picker shows high (found '$(Get-Value (Find-ById $window 'TaskReasoning'))')"

        # The second task asks for Codex with GPT-6-Sol at medium, which is not Codex's first model.
        Select-Element $sidebarRows[1]
        Check ((Wait-Until { (Get-Value $client) -like 'Codex*' }) -eq $true) "the client picker follows the second task (found '$(Get-Value $client)')"
        Check ((Get-Value (Find-ById $window 'TaskModel')) -match '^(GPT-6-Sol|gpt-6-sol .+)$') "choosing another task keeps its model (found '$(Get-Value (Find-ById $window 'TaskModel'))')"
        Check ((Get-Value (Find-ById $window 'TaskReasoning')) -eq 'medium') "choosing another task keeps its reasoning (found '$(Get-Value (Find-ById $window 'TaskReasoning'))')"
        Select-Element $sidebarRows[0]
        Check ((Wait-Until { (Get-Value $client) -like 'Claude Code*' }) -eq $true) "the client picker follows the first task again (found '$(Get-Value $client)')"
        Check ($window.Current.Name -eq 'agents-project - iDevelop') "choosing tasks in the sidebar edits nothing: '$($window.Current.Name)'"

        $entries = Wait-Until { @(Get-PickerEntries $client | Where-Object { $_.Current.Name -match '^(None|Claude Code|Codex|Pi|Antigravity CLI)( · .+)?$' }) | Where-Object { $_ } } 10
        Check (@($entries).Count -eq 5) "the client picker offers None and the four clients (found $(@($entries).Count))"
        Select-Element (@($entries) | Where-Object { $_.Current.Name -like 'Codex*' })
        try { $client.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
        Check ((Wait-Until { (Get-Value $client) -like 'Codex*' }) -eq $true) "choosing Codex selects it (found '$(Get-Value $client)')"
        Check ((Wait-Until { (Find-ById $window 'PermissionNote').Current.Name -like 'Codex may edit*' }) -eq $true) 'the permission note follows the chosen client'
        Check ((Wait-Until { $window.Current.Name -eq 'agents-project* - iDevelop' }) -eq $true) "choosing an agent is an unsaved edit: '$($window.Current.Name)'"
        Save-Screenshot $window (Join-Path $run 'pickers.png')
        Close-Window $window
        Invoke-Element (Find-InProcessWindows $process 'DiscardChanges')
    }

    # The fake Codex goes on an otherwise empty PATH and waits at a gate that the check never opens.
    $project = Join-Path $run 'run-project'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '../samples/storage-change') -Destination $project
    $fakeBin = Join-Path $run 'fake-bin'
    $sleeperPid = Join-Path $run 'sleeper.pid'
    $fakeAgent = New-FakeCodex $fakeBin (Join-Path $run 'never') $sleeperPid
    Check ([IO.File]::Exists($fakeAgent)) "the fake agent is built at $fakeAgent"
    $savedPath = $env:PATH
    $env:PATH = $fakeBin
    try {
        With-App $project {
            param($process, $window)
            Check ((Wait-Until { (Find-ById $window 'AgentCodex').Current.Name -like 'Ready*' } 60) -eq $true) 'the fake Codex is ready'
            # Claude Code is missing here, so the first task's picker lists only high, and the second task's GPT-6-Sol lists
            # high third. Moving between them makes Avalonia carry high over to the new list as a selection change.
            $sidebarRows = (Find-ById $window 'SidebarTasks').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
            Select-Element $sidebarRows[0]
            $reasoning = Find-ById $window 'TaskReasoning'
            Check ((Wait-Until { (Get-Value $reasoning) -eq 'high' }) -eq $true) "the reasoning picker shows the first task's high (found '$(Get-Value $reasoning)')"
            Select-Element $sidebarRows[1]
            Check ((Wait-Until { (Get-Value (Find-ById $window 'TaskClient')) -like 'Codex*' }) -eq $true) "the client picker follows the second task (found '$(Get-Value (Find-ById $window 'TaskClient'))')"
            Check ((Get-Value $reasoning) -eq 'medium') "the reasoning picker shows the second task's medium (found '$(Get-Value $reasoning)')"
            Check ($window.Current.Name -eq 'run-project - iDevelop') "choosing a task whose model offers the previous task's level edits nothing: '$($window.Current.Name)'"
            Invoke-Element (Find-ById $window 'RunTask')
            $bar = Find-ById $window 'RunBar'
            Check ($null -ne $bar) 'UI Automation finds the run bar'
            Check ((Find-ById $window 'RunBarTask').Current.Name -eq 'Implement atomic save') "the run bar names the running task (found '$((Find-ById $window 'RunBarTask').Current.Name)')"
            $sleeper = Wait-Until { if ([IO.File]::Exists($sleeperPid)) { [int]([IO.File]::ReadAllText($sleeperPid)) } } 30
            Check ($null -ne $sleeper) 'the fake Codex started a process whose parent exits'
            Save-Screenshot $window (Join-Path $run 'run-bar.png')

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
        Check ($null -eq (Get-SettingsText)) 'the earlier launches left no theme preference'
        Check (Test-Selected (Find-ById $window 'ThemeSystem')) 'without a preference the System segment is chosen'
        Select-Element (Find-ById $window 'ThemeLight')
        Check ((Wait-Until { (Get-SettingsTheme) -eq 'light' }) -eq $true) "choosing Light saves 'light' (found '$(Get-SettingsTheme)')"
        Invoke-Element (Find-ById $window 'FitToScreen')
        Start-Sleep -Milliseconds 500
        Save-Screenshot $window (Join-Path $run 'light.png')

        Select-Element (Find-ById $window 'ThemeDark')
        Check ((Wait-Until { (Get-SettingsTheme) -eq 'dark' }) -eq $true) "choosing Dark saves 'dark' (found '$(Get-SettingsTheme)')"
        Check (-not (Test-Selected (Find-ById $window 'ThemeLight'))) 'the Light segment is no longer chosen'
        Start-Sleep -Milliseconds 500
        Save-Screenshot $window (Join-Path $run 'dark.png')
        Close-Window $window
    }

    # The app writes this compactly, so a launch that rewrote the file would change these bytes.
    $handWritten = '{ "theme": "dark" }'
    [IO.File]::WriteAllText((Get-SettingsPath), $handWritten)
    With-App $project {
        param($process, $window)
        Check (Test-Selected (Find-ById $window 'ThemeDark')) 'Dark is still chosen after a restart'
        Check ((Get-SettingsText) -eq $handWritten) "a launch leaves the preference file byte for byte (found '$(Get-SettingsText)')"
        Select-Element (Find-ById $window 'ThemeSystem')
        Check ((Wait-Until { (Get-SettingsTheme) -eq 'system' }) -eq $true) "choosing System saves 'system' (found '$(Get-SettingsTheme)')"
        Close-Window $window
    }
} finally {
    Restore-Settings $run
}

$results | ForEach-Object { Write-Output $_ }

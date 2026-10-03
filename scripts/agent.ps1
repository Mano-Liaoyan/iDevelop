# A param block would bind client flags such as -p or -c to launcher or common parameters by prefix, so arguments come from $args.
$ErrorActionPreference = 'Stop'
$clients = 'codex', 'claude', 'pi', 'gemini'
$usage = "Usage: scripts/agent.ps1 <$($clients -join '|')> [client arguments]"
if ($args.Count -eq 0) { throw "Missing client. $usage" }
if ($args[0] -notin $clients) { throw "Unknown client '$($args[0])'. $usage" }
$Client = $args[0]
# PowerShell parses a bare a,b argument as an array. Join it with commas, as a direct native call does.
[string[]]$ClientArguments = @($args | Select-Object -Skip 1 | ForEach-Object { $_ -join ',' })
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
$previousGeminiHome = $env:GEMINI_CLI_HOME
try {
    if (!(Get-Command $Client -ErrorAction SilentlyContinue)) {
        throw "$Client is not installed or is not on PATH. See README.md."
    }
    & node (Join-Path $PSScriptRoot 'pstack.mjs') check
    if ($LASTEXITCODE -ne 0) { throw 'Run node scripts/pstack.mjs setup first.' }
    switch ($Client) {
        'codex' {
            & node (Join-Path $PSScriptRoot 'pstack.mjs') isolate-codex
            if ($LASTEXITCODE -ne 0) { throw 'Codex skill isolation failed. Check project trust and the audit output.' }
            $skillOverride = [IO.File]::ReadAllText((Join-Path $projectRoot '.pstack/local/codex-override.txt'))
            & codex -c $skillOverride @ClientArguments
        }
        'claude' {
            & claude --setting-sources project,local @ClientArguments
        }
        'pi' {
            & pi --no-extensions --no-skills --skill (Join-Path $projectRoot '.agents/skills') --no-prompt-templates @ClientArguments
        }
        'gemini' {
            $env:GEMINI_CLI_HOME = Join-Path $projectRoot '.pstack/runtime/gemini-home'
            New-Item -ItemType Directory -Force -Path $env:GEMINI_CLI_HOME | Out-Null
            $listing = (& gemini skills list --all | Out-String) -replace "`e\[[0-9;]*m", ''
            if ($LASTEXITCODE -ne 0) { throw 'Gemini skill discovery failed.' }
            $found = [regex]::Matches($listing, '(?m)^([a-z0-9][a-z0-9-]*) \[(Enabled|Disabled)\]')
            $expected = @(Get-ChildItem (Join-Path $projectRoot '.agents/skills') -Directory | ForEach-Object Name)
            $discovered = @($found | ForEach-Object { $_.Groups[1].Value })
            if (@($expected | Where-Object { $_ -notin $discovered }).Count -gt 0) {
                throw 'Gemini did not discover all PStack skills. Complete its project trust/onboarding in this isolated profile first; see README.md.'
            }
            foreach ($item in $found) {
                $skillName = $item.Groups[1].Value
                if ($skillName -notin $expected -and $item.Groups[2].Value -eq 'Enabled') {
                    & gemini skills disable $skillName --scope workspace
                    if ($LASTEXITCODE -ne 0) { throw "Could not disable $skillName for this workspace." }
                }
                if ($skillName -in $expected -and $item.Groups[2].Value -eq 'Disabled') {
                    throw "PStack skill $skillName is disabled. Enable it in this workspace before starting."
                }
            }
            $verifiedListing = (& gemini skills list --all | Out-String) -replace "`e\[[0-9;]*m", ''
            if ($LASTEXITCODE -ne 0) { throw 'Gemini verification failed.' }
            $enabledNames = @([regex]::Matches($verifiedListing, '(?m)^([a-z0-9][a-z0-9-]*) \[Enabled\]') | ForEach-Object { $_.Groups[1].Value })
            if (Compare-Object ($expected | Sort-Object) ($enabledNames | Sort-Object)) {
                throw 'Gemini enabled skills do not match PStack. Inspect gemini skills list --all.'
            }
            Write-Host 'Only PStack skills are enabled. Gemini management lists can still display disabled built-ins.'
            & gemini @ClientArguments
        }
    }
    $clientExit = $LASTEXITCODE
} finally {
    $env:GEMINI_CLI_HOME = $previousGeminiHome
    Pop-Location
}
exit $clientExit

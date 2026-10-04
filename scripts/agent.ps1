# A param block would bind client flags such as -p or -c to launcher or common parameters by prefix, so arguments come from $args.
$ErrorActionPreference = 'Stop'
$clients = 'codex', 'claude', 'pi', 'agy'
$usage = "Usage: scripts/agent.ps1 <$($clients -join '|')> [client arguments]"
if ($args.Count -eq 0) { throw "Missing client. $usage" }
if ($args[0] -notin $clients) { throw "Unknown client '$($args[0])'. $usage" }
$Client = $args[0]
# PowerShell parses a bare a,b argument as an array. Join it with commas, as a direct native call does.
[string[]]$ClientArguments = @($args | Select-Object -Skip 1 | ForEach-Object { $_ -join ',' })
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (!(Get-Command $Client -ErrorAction SilentlyContinue)) {
        throw "$Client is not installed or is not on PATH. See docs/development-environment.md."
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
        'agy' {
            $personalSources = @('config/skills', 'config/plugins', 'config/skills.json', 'config/plugins.json',
                'antigravity-cli/skills', 'antigravity-cli/plugins', 'skills') |
                ForEach-Object { Join-Path $env:USERPROFILE ".gemini/$_" } |
                Where-Object { (Test-Path $_ -PathType Leaf) -or @(Get-ChildItem $_ -Force -ErrorAction SilentlyContinue).Count -gt 0 }
            if ($personalSources) {
                throw "Antigravity CLI would load personal skills or plugins from $($personalSources -join ', '). It has no isolated profile; see docs/development-environment.md."
            }
            & agy @ClientArguments
        }
    }
    $clientExit = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $clientExit

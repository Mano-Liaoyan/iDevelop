# The theme switch

The `System`, `Light`, and `Dark` segments under `APPEARANCE` at the bottom of the sidebar set the app's theme. `System` follows the operating system and is the default. The app remembers the choice per user in `%APPDATA%\iDevelop\settings.json` and writes that file only when the user picks a segment.

## Sub-features

- `theme-default` marks `System` when no preference exists.
- `theme-pick` recolors the window and saves `light`, `dark`, or `system` when the user picks a segment.
- `theme-restart` marks the saved choice again after a restart, and the launch leaves the file unchanged.
- `theme-no-edit` changes no project, so the window title keeps no `*`.

## How to get to it (user POV)

- Choose `System`, `Light`, or `Dark` under `APPEARANCE` in the sidebar.
- Close iDevelop and start it again.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`. The session moved the user's preference aside, so `Get-SettingsText` returns nothing.
- Run each bullet in one call, so its variables stay set.

- **Default.** Run `Assert-Step $s ($null -eq (Get-SettingsText)) 'the session starts without a theme preference'` and `Assert-Step $s (Test-Selected (Find-ById $s.Window 'ThemeSystem')) 'System is chosen without a preference'`.
- **Light.** Run `Select-Element (Find-ById $s.Window 'ThemeLight')` and `Assert-Step $s ((Wait-Until { (Get-SettingsTheme) -eq 'light' }) -eq $true) 'choosing Light saves light'`. Run `Invoke-Element (Find-ById $s.Window 'FitToScreen')` and `Save-Evidence $s 'light'`. The screenshot shows a light sidebar, canvas, and inspector.
- **Dark.** Run `Select-Element (Find-ById $s.Window 'ThemeDark')`, `Assert-Step $s ((Wait-Until { (Get-SettingsTheme) -eq 'dark' }) -eq $true) 'choosing Dark saves dark'`, `Assert-Step $s (-not (Test-Selected (Find-ById $s.Window 'ThemeLight'))) 'Light is no longer chosen'`, and `Assert-Step $s ($s.Window.Current.Name -eq 'project - iDevelop') 'the theme edits no project'`. Run `Save-Evidence $s 'dark'`. The screenshot shows the dark theme.
- **Restart.** Close the window and reopen it in the same session. Run `$text = Get-SettingsText`, `Close-Window $s.Window`, `$s.Process.WaitForExit(15000)`, and `$s = Start-IDevelop -Reopen`. Then run `Assert-Step $s (Test-Selected (Find-ById $s.Window 'ThemeDark')) 'Dark is still chosen after a restart'` and `Assert-Step $s ((Get-SettingsText) -eq $text) 'the launch leaves the preference file unchanged'`.
- **System.** Run `Select-Element (Find-ById $s.Window 'ThemeSystem')` and `Assert-Step $s ((Wait-Until { (Get-SettingsTheme) -eq 'system' }) -eq $true) 'choosing System saves system'`.
- **Restore.** Run `$own = [IO.File]::ReadAllText("$(Get-SettingsPath).verify-backup")`, `Stop-IDevelop`, and `Assert-Step $s ("$(Get-SettingsText)" -eq $own) "Stop-IDevelop restores the user's preference"`. An empty backup means the user had no preference, and then `settings.json` is gone again.

## Gotchas

- The preference file is per user and shared with the user's own iDevelop. Never write it outside a session. `Stop-IDevelop` restores it.
- Reopen with `Start-IDevelop -Reopen`, not a new session. With the window closed, a new session takes this one's backup for a killed run's and restores the user's preference before it launches.
- `System` follows Windows' app mode, so its screenshot is light or dark depending on the machine.
- `ThemeTests` covers the recolored canvas colors and the file's parsing headlessly.

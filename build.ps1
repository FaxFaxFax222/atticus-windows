# Installs Atticus for this user: builds Atticus.exe, sets up its Python, starts it at login, and (re)starts it now.
# Run it again after any change:  powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$ProgressPreference = 'SilentlyContinue'
$local = Join-Path $env:LOCALAPPDATA 'Atticus'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Atticus'

if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'Atticus needs Windows 10 (2004) or later; Windows 11 looks best.' }
if (-not (Test-Path "$env:ProgramFiles\Git\bin\bash.exe") -and -not (Test-Path "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe")) {
  Write-Host 'Note: Git for Windows is not installed. Claude Code needs it: winget install Git.Git' -ForegroundColor Yellow
}

# Python 3.11+ for atticus.py, in its own venv
$py = $null
foreach ($c in @(@('py', '-3.13'), @('py', '-3.12'), @('py', '-3.11'), @('python'))) {
  try { $v = & $c[0] $c[1..9] -c 'import sys; print(sys.version_info >= (3, 11))' 2>$null; if ($v -eq 'True') { $py = $c; break } } catch {}
}
if (-not $py) { throw 'Atticus needs Python 3.11 or later: winget install Python.Python.3.12   then run build.ps1 again.' }
$venv = Join-Path $local 'venv'
if (-not (Test-Path "$venv\Scripts\python.exe")) { & $py[0] $py[1..9] -m venv $venv }
& "$venv\Scripts\python.exe" -m pip install -q --disable-pip-version-check -r requirements.txt
if ($LASTEXITCODE) { throw 'pip install failed' }

# .NET 10 SDK, for the app itself (installed per user if missing; no admin needed)
$dotnet = $null
foreach ($d in @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")) {
  if ($d -and (Test-Path $d) -and ((& $d --list-sdks) -match '^(1[0-9])\.')) { $dotnet = $d; break }
}
if (-not $dotnet) {
  Write-Host 'Installing the .NET 10 SDK for this user…'
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1"
  & powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\dotnet-install.ps1" -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet" | Out-Null
  $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

# A compiler error must leave the installed app alone.
$stage = Join-Path $env:TEMP ('atticus-build-' + [guid]::NewGuid())
& $dotnet publish win\Atticus -c Release -r win-x64 --self-contained -p:DebugType=none -o $stage --nologo -v q
if ($LASTEXITCODE) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue; throw 'Build failed; the installed Atticus is unchanged.' }

Get-Process Atticus -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$app*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $app, "$app\ui" | Out-Null
Copy-Item "$stage\*" $app -Recurse -Force
Remove-Item $stage -Recurse -Force
Copy-Item atticus.py, winctl.py $app -Force
Copy-Item ui\*.html, ui\*.js, ui\AppIcon.ico, ui\AppIcon.png "$app\ui" -Force
$version = (git log -1 --format='%h · %cd' --date=format:'%b %d, %Y' 2>$null)
@{ python = "$venv\Scripts\python.exe"; source = $PSScriptRoot; version = "$version" } | ConvertTo-Json | Set-Content "$app\atticus.json" -Encoding utf8

# Start menu shortcut, so "Atticus" opens Settings from Start
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Atticus.lnk")
$lnk.TargetPath = "$app\Atticus.exe"; $lnk.IconLocation = "$app\ui\AppIcon.ico"; $lnk.Description = 'Hold Ctrl+Alt+Space and talk'; $lnk.Save()

# Starts at login unless turned off in Settings
$prefs = Join-Path $env:APPDATA 'Atticus\settings.json'
$login = $true
if (Test-Path $prefs) { try { $p = Get-Content $prefs -Raw | ConvertFrom-Json; if ($p.login -eq $false) { $login = $false } } catch {} }
if ($login) { Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Atticus -Value "`"$app\Atticus.exe`"" }

if ($env:ATTICUS_NO_RESTART) { exit 0 }
Start-Process "$app\Atticus.exe"
Write-Host 'Atticus is running. Hold Ctrl+Alt+Space and talk.'

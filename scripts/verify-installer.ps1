param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$setup = Join-Path $artifacts "BackupCron-$Version-win-x64-setup.exe"
$install = Join-Path $env:RUNNER_TEMP 'BackupCron-installed-fixture'
$proof = Join-Path $artifacts 'proof'
New-Item -ItemType Directory -Force $proof | Out-Null
$process = Start-Process $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$install", "/LOG=$artifacts/install.log") -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Install failed: $($process.ExitCode)" }
$app = Join-Path $install 'BackupCron.exe'
if (-not (Test-Path $app)) { throw 'Installed app is missing.' }
if (-not (Test-Path (Join-Path $install 'LICENSE.txt'))) { throw 'MIT license missing from installation.' }
$process = Start-Process $app -ArgumentList @('--smoke-test', $proof) -PassThru
if (-not $process.WaitForExit(90000)) { $process.Kill(); throw 'Installed app smoke test timed out.' }
if ($process.ExitCode -ne 0) { Get-Content (Join-Path $proof 'smoke-result.txt'); throw 'Installed app smoke test failed.' }
Get-Content (Join-Path $proof 'smoke-result.txt')
$uninstaller = Join-Path $install 'unins000.exe'
$process = Start-Process $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
if ($process.ExitCode -ne 0 -or (Test-Path $app)) { throw 'Uninstall verification failed.' }
Write-Output 'PASS: install, launch, DPAPI, real file and SQLite backups, UI render and uninstall.'

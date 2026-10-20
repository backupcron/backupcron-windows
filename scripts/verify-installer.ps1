param([string]$Version = '1.3.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$setup = Join-Path $artifacts "BackupCron-$Version-win-x64-setup.exe"
$install = Join-Path $env:RUNNER_TEMP 'BackupCron-installed-fixture'
$proof = Join-Path $artifacts 'proof'
New-Item -ItemType Directory -Force $proof | Out-Null
# Validate a real upgrade with legacy user data on the disposable CI runner.
$previous = Join-Path $env:RUNNER_TEMP 'BackupCron-previous-setup.exe'
Invoke-WebRequest 'https://github.com/backupcron/backupcron-windows/releases/download/v1.1.0/BackupCron-1.1.0-win-x64-setup.exe' -OutFile $previous
if ((Get-FileHash $previous -Algorithm SHA256).Hash.ToLowerInvariant() -ne '6f82f3ee90129bea2a5e4bc6bbe67e35f749902474aa5042e99db59afdc1ac42') { throw 'Previous installer checksum mismatch.' }
$process = Start-Process $previous -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$install") -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Previous installer failed.' }
$profile = Join-Path $env:LOCALAPPDATA 'BackupCron'
if (Test-Path $profile) { throw 'Upgrade fixture requires a clean CI user profile.' }
New-Item -ItemType Directory $profile | Out-Null
$legacy = Join-Path $profile 'state.json'
Set-Content $legacy '{"version":1,"jobs":[],"history":[],"settings":{"schedulerEnabled":true}}' -Encoding utf8NoBOM
$legacyHash = (Get-FileHash $legacy -Algorithm SHA256).Hash
$process = Start-Process $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$install", "/LOG=$artifacts/install.log") -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Install failed: $($process.ExitCode)" }
if ((Get-FileHash $legacy -Algorithm SHA256).Hash -ne $legacyHash) { throw 'Upgrade changed legacy settings.' }
$app = Join-Path $install 'BackupCron.exe'
if (-not (Test-Path $app)) { throw 'Installed app is missing.' }
if (-not (Test-Path (Join-Path $install 'LICENSE.txt'))) { throw 'MIT license missing from installation.' }
$shareName = 'BackupCronCI-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$shareDirectory = Join-Path $env:RUNNER_TEMP $shareName
New-Item -ItemType Directory $shareDirectory | Out-Null
Start-Service LanmanServer
New-SmbShare -Name $shareName -Path $shareDirectory -FullAccess "$env:COMPUTERNAME\$env:USERNAME" -Temporary | Out-Null
try {
    $network = "\\localhost\$shareName"
    if (-not (Test-Path $network)) { throw 'Prepared SMB fixture is not accessible.' }
    $process = Start-Process $app -ArgumentList @('--smoke-test', $proof, '--storage-share', $network) -PassThru
    if (-not $process.WaitForExit(150000)) { $process.Kill(); throw 'Installed app smoke test timed out.' }
    if ($process.ExitCode -ne 0) { Get-Content (Join-Path $proof 'smoke-result.txt'); throw 'Installed app smoke test failed.' }
    Get-Content (Join-Path $proof 'smoke-result.txt')
    Get-Content (Join-Path $proof 'ui-result.txt')
} finally {
    Remove-SmbShare -Name $shareName -Force -Confirm:$false
}
$uninstaller = Join-Path $install 'unins000.exe'
$process = Start-Process $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
if ($process.ExitCode -ne 0 -or (Test-Path $app)) { throw 'Uninstall verification failed.' }
if ((Get-FileHash $legacy -Algorithm SHA256).Hash -ne $legacyHash) { throw 'Uninstall changed user data.' }
Write-Output 'PASS: upgrade from 1.1, preserved user settings, launch, DPAPI, file/SQLite/UNC backups, UI render, native storage capacity, tray warnings, next-backup budgets and uninstall.'

param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.3.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$publish = Join-Path $artifacts 'publish'
New-Item -ItemType Directory -Force $artifacts | Out-Null
dotnet publish (Join-Path $repo 'src/BackupCron.Windows/BackupCron.Windows.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$iscc = @('C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source }
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$artifacts" (Join-Path $repo 'installer/BackupCron.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $artifacts "BackupCron-$Version-win-x64-setup.exe"
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path (Join-Path $artifacts 'SHA256SUMS.txt') -Value "$hash  $(Split-Path $installer -Leaf)" -Encoding utf8NoBOM
Write-Output $installer

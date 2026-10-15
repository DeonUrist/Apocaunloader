param([string]$GameDir = 'E:\SteamLibrary\steamapps\common\Apocalypter')
$ErrorActionPreference = 'Stop'
$taskGame = (Resolve-Path -LiteralPath $GameDir).Path
$taskRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskRuntime = Join-Path $PSScriptRoot 'runtime-magazines-1.3.0'
$taskOutput = Join-Path $PSScriptRoot ('output\' + (Get-Date -Format yyyyMMdd-HHmmss))
dotnet build (Join-Path $taskRepo 'Apocaunloader.csproj') -c Release -p:DeployToGame=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
dotnet build (Join-Path $PSScriptRoot 'NativeVerifier.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Verifier build failed' }
New-Item -ItemType Directory -Path "$taskRuntime\BepInEx\plugins", "$taskRuntime\BepInEx\config", $taskOutput -Force | Out-Null
foreach ($file in @('Apocalypter.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini','.doorstop_version')) { Copy-Item -LiteralPath "$taskGame\$file" -Destination $taskRuntime -Force }
Copy-Item -LiteralPath "$taskGame\BepInEx\core" -Destination "$taskRuntime\BepInEx" -Recurse -Force
foreach ($dir in @('Apocalypter_Data','MonoBleedingEdge')) { if (!(Test-Path -LiteralPath "$taskRuntime\$dir")) { New-Item -ItemType Junction -Path "$taskRuntime\$dir" -Target "$taskGame\$dir" | Out-Null } }
Copy-Item -LiteralPath "$taskRepo\bin\Release\Apocaunloader.dll", "$PSScriptRoot\bin\Release\net472\ApocaunloaderNativeVerifier.dll" -Destination "$taskRuntime\BepInEx\plugins" -Force
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
LogLevels = All
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\BepInEx.cfg"
$taskArgs = @('-batchmode','-nographics','-logFile',('"' + $taskRuntime + '\unity.log"'),('-magazine-test-root="' + $taskOutput + '"'))
$taskProcess = Start-Process -FilePath "$taskRuntime\Apocalypter.exe" -WorkingDirectory $taskRuntime -WindowStyle Hidden -ArgumentList $taskArgs -PassThru
try {
    $taskDeadline = (Get-Date).AddMinutes(3)
    while (!$taskProcess.WaitForExit(1000)) { if ((Get-Date) -gt $taskDeadline) { throw 'Native verifier timed out' } }
    $taskReport = Join-Path $taskOutput 'report.txt'
    if (!(Test-Path -LiteralPath $taskReport)) { throw 'Native verifier produced no report' }
    Get-Content -LiteralPath $taskReport
    Write-Output "Report: $taskReport"
    if (Select-String -LiteralPath $taskReport -Pattern '^FAIL:') { throw 'Native verifier failed' }
} finally { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id } }

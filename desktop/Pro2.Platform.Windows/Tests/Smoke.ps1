param([Parameter(Mandatory=$true)][string]$HostExecutable)
$ErrorActionPreference = 'Stop'
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Resolve-Path $HostExecutable).Path
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$requests = @(
    @{command='ping';id='ping'},
    @{command='selfTest';id='selftest'},
    @{command='settings';runtimebattery=$true;autooptimize=$true;autoReconnect=$false;skipInitialization=$true;inputTransport='auto';id='settings'},
    @{command='backendStatus';id='backend'},
    @{command='disconnect';slot=4;id='invalid-slot'},
    @{command='unrecognized';id='unknown-command'},
    @{command='readActualOutput';index=4;id='invalid-xinput'},
    @{command='configureBluetooth';id='missing-preset'},
    @{command='switchToUsb';slot=3;deviceId='unobserved-usb';id='invalid-handover'},
    @{command='stop';id='stop'}
)
foreach ($request in $requests) { $process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress)) }
$process.StandardInput.Close()
if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Native-host EOF cleanup exceeded 30 seconds.' }
$lines = $stdout.GetAwaiter().GetResult() -split "`n" | Where-Object { $_.Trim().Length -gt 0 }
$events = @($lines | ForEach-Object { $_ | ConvertFrom-Json })
if ($process.ExitCode -ne 0) { throw ($stderr.GetAwaiter().GetResult()) }
function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
Require (@($events | Where-Object { $_.event -eq 'ready' -and $_.protocolVersion -eq 1 -and $_.slots -eq 4 }).Count -eq 1) 'Missing protocol-v1 ready event.'
Require (@($events | Where-Object { $_.id -eq 'ping' -and $_.ok }).Count -eq 1) 'Ping correlation failed.'
Require (@($events | Where-Object { $_.id -eq 'selftest' -and $_.profiles -eq 6 -and $_.ok -and $_.transportSwitchPolicyTested }).Count -eq 1) 'Official six-profile packet and USB handover policy self-test failed.'
Require (@($events | Where-Object { $_.id -eq 'settings' -and $_.ok -and -not $_.autoReconnect -and $_.skip -and $_.inputTransport -eq 'auto' -and $_.initializationAppliesTo -eq 'nextConnection' }).Count -eq 1) 'Settings application failed.'
Require (@($events | Where-Object { $_.id -eq 'backend' -and $_.event -eq 'backendStatus' }).Count -eq 1) 'Real backend preflight failed.'
Require (@($events | Where-Object { $_.id -eq 'invalid-slot' -and $_.code -eq 'invalid_slot' }).Count -eq 1) 'Invalid slot guard failed.'
Require (@($events | Where-Object { $_.id -eq 'unknown-command' -and $_.code -eq 'unknown_command' }).Count -eq 1) 'Unknown command guard failed.'
Require (@($events | Where-Object { $_.id -eq 'invalid-xinput' -and $_.code -eq 'invalid_index' }).Count -eq 1) 'XInput index guard failed.'
Require (@($events | Where-Object { $_.id -eq 'missing-preset' -and $_.code -eq 'preset_required' }).Count -eq 1) 'Global configuration requires an explicit preset.'
Require (@($events | Where-Object { $_.id -eq 'invalid-handover' -and $_.code -eq 'usb_switch_failed' -and $_.slot -eq 3 }).Count -eq 1) 'USB handover failure correlation failed.'
Require (@($events | Where-Object { $_.event -eq 'stopped' }).Count -eq 1) 'EOF resource cleanup did not complete.'
Write-Output 'PASS Windows JSONL: ready, correlation, six real packet profiles, settings, backend preflight, error guards, EOF cleanup.'

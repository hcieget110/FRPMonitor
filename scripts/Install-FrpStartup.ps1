#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$FrpDirectory,
    [string]$TaskName = '开启FRP'
)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $FrpDirectory).Path
foreach ($name in @('frpc.exe', 'frpc.toml')) {
    if (-not (Test-Path -LiteralPath (Join-Path $directory $name) -PathType Leaf)) { throw ('Required local file missing: ' + $name) }
}
# Never overwrite a user's existing FRP task with a generic template.
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    throw ('Task already exists: ' + $TaskName + '. Keep your existing task or choose a different TaskName.')
}
$launcher = Join-Path $directory 'start-frpc-direct.cmd'
if ($PSCmdlet.ShouldProcess($TaskName, 'Install SYSTEM boot task and proxy-free launcher')) {
    if (Test-Path -LiteralPath $launcher) {
        $backup = $launcher + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
        Copy-Item -LiteralPath $launcher -Destination $backup
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-frpc-direct.cmd') -Destination $launcher -Force
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\cmd.exe') -Argument ('/d /c ""' + $launcher + '""') -WorkingDirectory $directory
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'FRP direct startup: no user logon or local proxy required.' | Out-Null
    Write-Output ('Created boot task: ' + $TaskName + '. This script does not start or stop FRP now.')
}

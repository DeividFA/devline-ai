<#
.SYNOPSIS
Reopens missing named orq runners using existing launcher files.
.EXAMPLE
.\scripts\watchdog.ps1 -OrqDir 'C:\src\my-app\.orq' -Launchers @{ 'Lane A' = 'frente-A.cmd'; 'Lane B' = 'frente-B.cmd' }
.DESCRIPTION
Run periodically with Windows Task Scheduler (for example, every ten minutes).
Launchers must start orq with --nome matching their map key. Values are launcher
paths relative to OrqDir, or absolute paths. A legacy " - ..." name suffix is
ignored when identifying live runners. Create vigia.pausado in OrqDir to pause.
The watchdog only reopens launchers; it does not close processes or edit tasks.
#>
[CmdletBinding()]
param(
    [string]$OrqDir = 'C:\src\my-app\.orq',
    [System.Collections.IDictionary]$Launchers = [ordered]@{
        'Lane A' = 'frente-A.cmd'
        'Lane B' = 'frente-B.cmd'
    }
)

$log = Join-Path $OrqDir 'vigia.log'
if (Test-Path -LiteralPath (Join-Path $OrqDir 'vigia.pausado')) { exit 0 }
$liveNames = @(Get-CimInstance Win32_Process -Filter "Name='orq.exe'" |
    ForEach-Object {
        if ($_.CommandLine -match '(?:^|\s)--nome\s+(?:"([^"]+)"|(\S+))') {
            $runnerName = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
            ($runnerName -replace ' - .*$', '').Trim()
        }
    })

foreach ($name in $Launchers.Keys) {
    if ($liveNames -notcontains $name) {
        $launcher = [string]$Launchers[$name]
        if (-not [System.IO.Path]::IsPathRooted($launcher)) {
            $launcher = Join-Path $OrqDir $launcher
        }
        if (Test-Path -LiteralPath $launcher -PathType Leaf) {
            Start-Process -FilePath explorer.exe -ArgumentList ('"{0}"' -f $launcher) -WindowStyle Hidden
            Add-Content -LiteralPath $log -Value ('{0:dd/MM HH:mm} reopened: {1}' -f (Get-Date), $name)
            Start-Sleep -Seconds 6
        }
    }
}

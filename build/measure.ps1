param(
    [int]$DurationMinutes = 10,
    [int]$IntervalSeconds = 5,
    [string]$OutputPath = "",
    [string[]]$ProcessNames = @("AzureGameAssistant", "AzureGameAssistant.Collector"),
    [switch]$IncludeSensors
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ($IncludeSensors -and ($ProcessNames -notcontains "AzureGameAssistant.Sensors")) {
    $ProcessNames += "AzureGameAssistant.Sensors"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path $root "artifacts\measurements\measure-$stamp.csv"
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$duration = [TimeSpan]::FromMinutes([Math]::Max(1, $DurationMinutes))
$interval = [TimeSpan]::FromSeconds([Math]::Max(1, $IntervalSeconds))
$logicalCores = [Environment]::ProcessorCount
$started = Get-Date
$previous = @{}
$rows = New-Object System.Collections.Generic.List[object]

Write-Host "Measuring for $($duration.TotalMinutes) minute(s), sampling every $($interval.TotalSeconds)s."
Write-Host "Output: $OutputPath"

while ((Get-Date) - $started -lt $duration) {
    $timestamp = Get-Date
    $elapsed = $timestamp - $started

    $perfRows = @{}
    try {
        foreach ($perf in Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -ErrorAction Stop) {
            $perfRows[[int]$perf.IDProcess] = $perf
        }
    } catch {
    }

    foreach ($name in $ProcessNames) {
        $process = Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $process) {
            continue
        }

        $cpuPercent = $null
        if ($previous.ContainsKey($name)) {
            $before = $previous[$name]
            $seconds = ($timestamp - $before.Time).TotalSeconds
            if ($seconds -gt 0) {
                $delta = ($process.TotalProcessorTime - $before.Cpu).TotalSeconds
                $cpuPercent = [Math]::Round(($delta / $seconds / $logicalCores) * 100, 3)
            }
        }

        $previous[$name] = [pscustomobject]@{ Time = $timestamp; Cpu = $process.TotalProcessorTime }

        $privateWorkingSetMb = $null
        if ($perfRows.ContainsKey($process.Id)) {
            $privateWorkingSetMb = [Math]::Round([double]$perfRows[$process.Id].WorkingSetPrivate / 1MB, 2)
        }

        $row = [pscustomobject]@{
            Timestamp         = $timestamp.ToString("o")
            ElapsedSeconds    = [Math]::Round($elapsed.TotalSeconds, 1)
            Process           = $name
            Pid               = $process.Id
            WorkingSetMb      = [Math]::Round($process.WorkingSet64 / 1MB, 2)
            PrivateWorkingSetMb = $privateWorkingSetMb
            PrivateMb         = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
            CpuPercent        = $cpuPercent
            TotalCpuSeconds   = [Math]::Round($process.TotalProcessorTime.TotalSeconds, 3)
            HandleCount       = $process.HandleCount
            ThreadCount       = $process.Threads.Count
        }

        $rows.Add($row)
        $cpuText = if ($null -eq $cpuPercent) { "n/a" } else { "$cpuPercent%" }
        Write-Host ("{0,-38} pid={1,-6} ws={2,8:N1} MB  privateWS={3,8:N1} MB  commit={4,8:N1} MB  cpu={5}" -f `
            $name, $process.Id, $row.WorkingSetMb, $row.PrivateWorkingSetMb, $row.PrivateMb, $cpuText)
    }

    Start-Sleep -Seconds $IntervalSeconds
}

$rows | Export-Csv -Path $OutputPath -NoTypeInformation -Encoding UTF8

$summaryPath = [System.IO.Path]::ChangeExtension($OutputPath, ".summary.txt")
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Measured: $($started.ToString('o')) for $($duration.TotalMinutes) minute(s)")
$lines.Add("Logical cores: $logicalCores")
$lines.Add("")

function Get-Median([double[]]$values) {
    if (-not $values -or $values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) {
        return $sorted[$middle]
    }

    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

foreach ($group in ($rows | Group-Object Process)) {
    $privateValues = @($group.Group | Where-Object { $null -ne $_.PrivateMb } | ForEach-Object { [double]$_.PrivateMb })
    $privateWorkingValues = @($group.Group | Where-Object { $null -ne $_.PrivateWorkingSetMb } | ForEach-Object { [double]$_.PrivateWorkingSetMb })
    $workingValues = @($group.Group | Where-Object { $null -ne $_.WorkingSetMb } | ForEach-Object { [double]$_.WorkingSetMb })
    $cpuValues = @($group.Group | Where-Object { $null -ne $_.CpuPercent } | ForEach-Object { [double]$_.CpuPercent })
    $handleValues = @($group.Group | Where-Object { $null -ne $_.HandleCount } | ForEach-Object { [double]$_.HandleCount })

    $line = "{0}: samples={1}  WS median={2:N1} MB  privateWS median={3:N1} MB  commit median={4:N1} MB  CPU median={5:N3}%  handles={6}" -f `
        $group.Name,
        $group.Count,
        (Get-Median $workingValues),
        (Get-Median $privateWorkingValues),
        (Get-Median $privateValues),
        (Get-Median $cpuValues),
        (Get-Median $handleValues)

    $lines.Add($line)
    Write-Host $line
}

$lines | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host "Summary: $summaryPath"

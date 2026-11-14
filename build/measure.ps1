param(
    [int]$DurationMinutes = 10,
    [int]$IntervalSeconds = 5,
    [string]$OutputPath = "",
    [string[]]$ProcessNames = @("AzureGameAssistant", "AzureGameAssistant.Collector"),
    [switch]$IncludeSensors,
    [ValidateSet("auto", "tray", "main", "deep", "highload", "none")]
    [string]$Mode = "auto"
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

function Get-MainWindowVisible {
    $process = Get-Process -Name "AzureGameAssistant" -ErrorAction SilentlyContinue | Select-Object -First 1
    return ($null -ne $process -and $process.MainWindowHandle -ne [IntPtr]::Zero)
}

if ($Mode -eq "auto") {
    $effectiveMode = if (Get-MainWindowVisible) { "main" } else { "tray" }
} else {
    $effectiveMode = $Mode
}

$physicalMb = 0
try {
    $physicalMb = [double](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory / 1MB
} catch {
}

# Budgets follow the confirmed performance red lines in docs/handoff.md §6:
# collector shallow <=30MB / <=0.3% CPU, deep <=45MB / <=0.5% (combined <=1.0%),
# UI tray <=60MB, UI main window <=80MB, high-load shed total <1% physical RAM.
$budgets = New-Object System.Collections.Generic.List[object]
function Add-Budget([string]$Process, [string]$Metric, [double]$Limit) {
    $budgets.Add([pscustomobject]@{ Process = $Process; Metric = $Metric; Limit = $Limit })
}

switch ($effectiveMode) {
    "tray" {
        Add-Budget "AzureGameAssistant" "PrivateWorkingSetMb" 60
        Add-Budget "AzureGameAssistant.Collector" "PrivateWorkingSetMb" 30
        Add-Budget "AzureGameAssistant.Collector" "CpuPercent" 0.3
    }
    "main" {
        Add-Budget "AzureGameAssistant" "PrivateWorkingSetMb" 80
        Add-Budget "AzureGameAssistant.Collector" "PrivateWorkingSetMb" 30
        Add-Budget "AzureGameAssistant.Collector" "CpuPercent" 0.3
    }
    "deep" {
        Add-Budget "AzureGameAssistant" "PrivateWorkingSetMb" 80
        Add-Budget "AzureGameAssistant.Collector" "PrivateWorkingSetMb" 45
        Add-Budget "AzureGameAssistant.Collector" "CpuPercent" 0.5
        Add-Budget "Total" "CpuPercent" 1.0
    }
    "highload" {
        if ($physicalMb -gt 0) {
            Add-Budget "Total" "PrivateWorkingSetMb" [Math]::Round($physicalMb * 0.01, 1)
        }
    }
}

$duration = [TimeSpan]::FromMinutes([Math]::Max(1, $DurationMinutes))
$interval = [TimeSpan]::FromSeconds([Math]::Max(1, $IntervalSeconds))
$logicalCores = [Environment]::ProcessorCount
$started = Get-Date
$previous = @{}
$rows = New-Object System.Collections.Generic.List[object]

Write-Host "Measuring for $($duration.TotalMinutes) minute(s), sampling every $($interval.TotalSeconds)s."
Write-Host "Mode: $effectiveMode (requested: $Mode)"
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
$lines.Add("Mode: $effectiveMode (requested: $Mode)")
$lines.Add("Logical cores: $logicalCores")
if ($physicalMb -gt 0) {
    $lines.Add("Physical memory: $([Math]::Round($physicalMb, 0)) MB")
}
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

$medians = @{}
foreach ($group in ($rows | Group-Object Process)) {
    $privateValues = @($group.Group | Where-Object { $null -ne $_.PrivateMb } | ForEach-Object { [double]$_.PrivateMb })
    $privateWorkingValues = @($group.Group | Where-Object { $null -ne $_.PrivateWorkingSetMb } | ForEach-Object { [double]$_.PrivateWorkingSetMb })
    $workingValues = @($group.Group | Where-Object { $null -ne $_.WorkingSetMb } | ForEach-Object { [double]$_.WorkingSetMb })
    $cpuValues = @($group.Group | Where-Object { $null -ne $_.CpuPercent } | ForEach-Object { [double]$_.CpuPercent })
    $handleValues = @($group.Group | Where-Object { $null -ne $_.HandleCount } | ForEach-Object { [double]$_.HandleCount })

    $medians[$group.Name] = [pscustomobject]@{
        PrivateWorkingSetMb = Get-Median $privateWorkingValues
        WorkingSetMb        = Get-Median $workingValues
        PrivateMb           = Get-Median $privateValues
        CpuPercent          = Get-Median $cpuValues
        HandleCount         = Get-Median $handleValues
    }

    $line = "{0}: samples={1}  WS median={2:N1} MB  privateWS median={3:N1} MB  commit median={4:N1} MB  CPU median={5:N3}%  handles={6}" -f `
        $group.Name,
        $group.Count,
        $medians[$group.Name].WorkingSetMb,
        $medians[$group.Name].PrivateWorkingSetMb,
        $medians[$group.Name].PrivateMb,
        $medians[$group.Name].CpuPercent,
        $medians[$group.Name].HandleCount

    $lines.Add($line)
    Write-Host $line
}

$budgetLines = New-Object System.Collections.Generic.List[string]
$budgetFailures = 0
$missing = 0
$totals = [pscustomobject]@{ PrivateWorkingSetMb = 0.0; CpuPercent = 0.0 }

foreach ($name in $ProcessNames) {
    $median = $medians[$name]
    if ($null -eq $median -or $null -eq $median.PrivateWorkingSetMb) {
        $missing++
        $budgetLines.Add("MISSING $name (no samples)")
        continue
    }

    $totals.PrivateWorkingSetMb += [double]$median.PrivateWorkingSetMb
    if ($null -ne $median.CpuPercent) {
        $totals.CpuPercent += [double]$median.CpuPercent
    }
}

foreach ($budget in $budgets) {
    if ($budget.Process -eq "Total") {
        $actual = if ($budget.Metric -eq "CpuPercent") { $totals.CpuPercent } else { $totals.PrivateWorkingSetMb }
    } elseif ($null -ne $medians[$budget.Process]) {
        $actual = $medians[$budget.Process].($budget.Metric)
    } else {
        $actual = $null
    }

    if ($null -eq $actual) {
        $budgetLines.Add("SKIP $($budget.Process) $($budget.Metric): no data")
        continue
    }

    $unit = if ($budget.Metric -eq "CpuPercent") { "%" } else { " MB" }
    if ([double]$actual -le $budget.Limit) {
        $budgetLines.Add(("PASS {0} {1} = {2:N3}{3} (limit {4:N3}{3})" -f `
            $budget.Process, $budget.Metric, $actual, $unit, $budget.Limit))
    } else {
        $budgetFailures++
        $budgetLines.Add(("FAIL {0} {1} = {2:N3}{3} (limit {4:N3}{3})" -f `
            $budget.Process, $budget.Metric, $actual, $unit, $budget.Limit))
    }
}

$lines.Add("")
$lines.Add("Budgets:")
$lines.AddRange($budgetLines)
foreach ($line in $budgetLines) {
    Write-Host $line
}

$lines | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host "Summary: $summaryPath"

if ($missing -gt 0) {
    Write-Host "RESULT INCOMPLETE: $missing process(es) produced no samples."
    exit 2
}

if ($budgetFailures -gt 0) {
    Write-Host "RESULT FAIL: $budgetFailures budget(s) exceeded."
    exit 1
}

Write-Host "RESULT PASS"
exit 0

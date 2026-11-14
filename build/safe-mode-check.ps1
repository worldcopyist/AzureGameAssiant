param(
    [string]$ExePath = "",
    [int]$StartupTimeoutSeconds = 40
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $root "src\AzureGameAssistant.UI\bin\Release\net8.0-windows\AzureGameAssistant.exe"
}
if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath (run dotnet build -c Release first)"
}

$script:failures = New-Object System.Collections.Generic.List[string]

function Write-Log([string]$message) {
    Write-Host ("{0} {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $message)
}

function Check([bool]$condition, [string]$description) {
    if ($condition) {
        Write-Log "PASS $description"
    }
    else {
        $script:failures.Add($description)
        Write-Log "FAIL $description"
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ById([System.Windows.Automation.AutomationElement]$parent, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ForMainWindow([int]$processId, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        foreach ($window in $windows) {
            if ($null -ne (Find-ById $window "SettingsButton")) {
                return $window
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

$existing = Get-Process -Name "AzureGameAssistant" -ErrorAction SilentlyContinue
if ($existing) {
    throw "AzureGameAssistant is already running (PID $($existing.Id -join ', ')). Stop it before running this check."
}

$appProcess = $null
try {
    Write-Log "launch $ExePath --safe-mode --no-elevate"
    $appProcess = Start-Process -FilePath $ExePath -ArgumentList "--safe-mode", "--no-elevate" -PassThru

    $mainWindow = Wait-ForMainWindow $appProcess.Id $StartupTimeoutSeconds
    Check ($null -ne $mainWindow) "main window appears"
    if ($null -eq $mainWindow) {
        throw "Main window did not appear"
    }

    $panel = Find-ById $mainWindow "SafeModePanel"
    Check ($null -ne $panel) "SafeModePanel visible"
    if ($null -ne $panel) {
        $panelText = $panel.Current.Name
        Write-Log "safe mode banner: $panelText"
        Check ($panelText -like "*安全模式*") "safe mode banner reports safe mode"
    }

    $badge = Find-ById $mainWindow "QualityBadge"
    Check ($null -ne $badge) "QualityBadge present"
    if ($null -ne $badge) {
        $badgeText = $badge.Current.Name
        Write-Log "quality badge: $badgeText"
        Check ($badgeText -like "*浅模式*") "quality badge reports shallow mode"
    }

    $collector = $null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $collector = Get-CimInstance Win32_Process -Filter "Name = 'AzureGameAssistant.Collector.exe'" | Select-Object -First 1
        if ($null -ne $collector) { break }
        Start-Sleep -Milliseconds 300
    }
    Check ($null -ne $collector) "collector started"
    if ($null -ne $collector) {
        Write-Log "collector cmdline: $($collector.CommandLine)"
        Check ($collector.CommandLine -match "--safe-mode") "collector launched with --safe-mode"
    }

    Start-Sleep -Seconds 2
    $sensors = Get-Process -Name "AzureGameAssistant.Sensors" -ErrorAction SilentlyContinue
    Check ($null -eq $sensors) "sensor sidecar is not started"
    $presentMon = Get-Process -Name "PresentMon*" -ErrorAction SilentlyContinue
    Check ($null -eq $presentMon) "PresentMon is not started"
}
catch {
    $script:failures.Add($_.Exception.Message)
    Write-Log "ERROR $($_.Exception.Message)"
}
finally {
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(5000)) {
            Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Get-Process -Name "AzureGameAssistant.Collector" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if ($script:failures.Count -gt 0) {
        Write-Log ("RESULT FAIL ({0} checks failed)" -f $script:failures.Count)
        exit 1
    }

    Write-Log "RESULT PASS"
    exit 0
}

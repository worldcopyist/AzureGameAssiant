param(
    [string]$ExePath = "",
    [int]$StartupTimeoutSeconds = 40,
    [string]$OutDir = "artifacts/ui-smoke"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $root "src\AzureGameAssistant.UI\bin\Release\net8.0-windows\AzureGameAssistant.exe"
}
if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath (run dotnet build -c Release first)"
}

$outPath = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$logPath = Join-Path $outPath "ui-smoke.log"
$screenshotMain = Join-Path $outPath "main.png"
$screenshotSettings = Join-Path $outPath "settings.png"
Remove-Item $logPath, $screenshotMain, $screenshotSettings -ErrorAction SilentlyContinue

$script:failures = New-Object System.Collections.Generic.List[string]

function Write-Log([string]$message) {
    $line = "{0} {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $message
    Write-Host $line
    Add-Content -Path $logPath -Value $line -Encoding UTF8
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
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace Win32 -Name Native -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.IntPtr dwExtraInfo);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);
"@

# Screen coordinates from UIA are physical pixels; make the capture process match
# so screenshots are not cropped or offset on scaled monitors.
[Win32.Native]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Find-ById([System.Windows.Automation.AutomationElement]$parent, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ForId([System.Windows.Automation.AutomationElement]$parent, [string]$automationId, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $element = Find-ById $parent $automationId
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
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

function Get-TopWindow([System.Windows.Automation.AutomationElement]$element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $current = $element
    while ($null -ne $current) {
        if ($current.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) {
            return $current
        }
        $current = $walker.GetParent($current)
    }
    return $null
}

function Click-Element([System.Windows.Automation.AutomationElement]$element) {
    $rect = $element.Current.BoundingRectangle
    $x = [int]($rect.X + ($rect.Width / 2))
    $y = [int]($rect.Y + ($rect.Height / 2))
    [Win32.Native]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 120
    [Win32.Native]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Win32.Native]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
}

function Save-WindowShot([System.Windows.Automation.AutomationElement]$window, [string]$path) {
    $rect = $window.Current.BoundingRectangle
    $width = [int][Math]::Max(1, $rect.Width)
    $height = [int][Math]::Max(1, $rect.Height)
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$existing = Get-Process -Name "AzureGameAssistant" -ErrorAction SilentlyContinue
if ($existing) {
    throw "AzureGameAssistant is already running (PID $($existing.Id -join ', ')). Stop it before running the smoke test."
}

$appProcess = $null
try {
    Write-Log "launch $ExePath --no-elevate"
    $appProcess = Start-Process -FilePath $ExePath -ArgumentList "--no-elevate" -PassThru

    $mainWindow = Wait-ForMainWindow $appProcess.Id $StartupTimeoutSeconds
    Check ($null -ne $mainWindow) "main window with SettingsButton appears"
    if ($null -eq $mainWindow) {
        throw "Main window did not appear"
    }

    $settingsButton = Find-ById $mainWindow "SettingsButton"
    $pauseButton = Find-ById $mainWindow "PauseButton"
    $aiButton = Find-ById $mainWindow "AiAnalyzeButton"
    $chart = Find-ById $mainWindow "CombinedChart"
    Check ($null -ne $pauseButton) "PauseButton present"
    Check ($null -ne $aiButton) "AiAnalyzeButton present"
    Check ($null -ne $chart) "CombinedChart present"

    if ($null -ne $chart) {
        $deadline = (Get-Date).AddSeconds(20)
        $points = 0
        while ((Get-Date) -lt $deadline) {
            $help = $chart.Current.HelpText
            if ($help -match "points=(\d+)") {
                $points = [int]$Matches[1]
                if ($points -gt 0) { break }
            }
            Start-Sleep -Milliseconds 300
        }
        Check ($points -gt 0) "chart received samples (points=$points)"

        if ($points -gt 0) {
            Click-Element $chart
            Start-Sleep -Milliseconds 500
            $pausedHelp = $chart.Current.HelpText
            Check ($pausedHelp -like "Paused;*") "click pauses the timeline ($pausedHelp)"

            $mainRect = $mainWindow.Current.BoundingRectangle
            [Win32.Native]::SetCursorPos([int]($mainRect.X + 6), [int]($mainRect.Y + 6)) | Out-Null
            Start-Sleep -Milliseconds 2300
            $liveHelp = $chart.Current.HelpText
            Check ($liveHelp -like "Live;*") "leaving the chart resumes after 1.5 s ($liveHelp)"
        }
    }

    Save-WindowShot $mainWindow $screenshotMain
    Check (Test-Path $screenshotMain) "main screenshot saved"

    if ($null -ne $settingsButton) {
        Click-Element $settingsButton
        $applyButton = Wait-ForId ([System.Windows.Automation.AutomationElement]::RootElement) "SettingsApply" 6
        Check ($null -ne $applyButton) "settings window opens"
        if ($null -ne $applyButton) {
            $settingsWindow = Get-TopWindow $applyButton
            if ($null -ne $settingsWindow) {
                Save-WindowShot $settingsWindow $screenshotSettings
                Check (Test-Path $screenshotSettings) "settings screenshot saved"

                $cancelButton = Find-ById $settingsWindow "SettingsCancel"
                if ($null -ne $cancelButton) {
                    Click-Element $cancelButton
                    Start-Sleep -Milliseconds 600
                }
            }
            Check ($null -eq (Find-ById ([System.Windows.Automation.AutomationElement]::RootElement) "SettingsApply")) "settings window closes"
        }
    }

    $mainWindow = Wait-ForMainWindow $appProcess.Id 5
    Check ($null -ne $mainWindow) "main window still alive after settings"
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
            Write-Log "force stopped main window"
        }
    }

    if ($script:failures.Count -gt 0) {
        Write-Log ("RESULT FAIL ({0} checks failed)" -f $script:failures.Count)
        exit 1
    }

    Write-Log "RESULT PASS"
    exit 0
}

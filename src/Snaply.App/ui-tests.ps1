param([Parameter(Mandatory)][int]$AppPid)

# Manual UI smoke test for the content-centric layout (run with the `winapp ui` tooling).
# NOT wired into CI — it drives the live app by AutomationId. After the settings-zero redesign:
#   - the header row is gone; the primary capture control is a SplitButton
#     (PrimaryCaptureButton in the floating toolbar), with per-mode menu items
#     CaptureFullScreenItem / CaptureRegionItem / CaptureWindowItem;
#   - Save/Copy float over the preview (PrimaryExportButton);
#   - there is no Settings dialog, no beautify toggle and no About button (Snaply always
#     beautifies; app details live in the bundled files). The command bar is just Capture +
#     Save/Copy. Theme and language follow the OS (no in-app switch).

$ErrorActionPreference = 'Continue'
$pass = 0; $fail = 0; $results = @()

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++; $script:results += @{ name = $Name; status = "PASS" }
            Write-Host "  PASS: $Name" -ForegroundColor Green
        } else {
            $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" }
            Write-Host "  FAIL: $Name -- $output" -ForegroundColor Red
        }
    } catch {
        $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL: $Name -- $_" -ForegroundColor Red
    }
}

New-Item -ItemType Directory -Force -Path "screenshots" | Out-Null

# --- 1. Persistent command bar / chrome AutomationIds resolve ---
$ids = @("PrimaryCaptureButton", "StatusText", "PreviewImage")
foreach ($id in $ids) {
    Test-UI "AutomationId resolves: $id" { winapp ui wait-for $id -a $AppPid -t 4000 }
}
winapp ui screenshot -a $AppPid -o "screenshots/01-empty.png" 2>$null

# --- 2. Capture full screen via the SplitButton menu (no overlay) -> preview populates ---
Test-UI "Open capture menu" { winapp ui expand "PrimaryCaptureButton" -a $AppPid }
Start-Sleep -Milliseconds 600
Test-UI "Invoke CaptureFullScreenItem" { winapp ui invoke "CaptureFullScreenItem" -a $AppPid }
Start-Sleep -Seconds 2
Test-UI "StatusText shows px dimensions" { winapp ui wait-for "StatusText" -a $AppPid --value "px" --contains -t 5000 }

# --- 3. Export SplitButton appears and is enabled after capture ---
Test-UI "PrimaryExportButton present" { winapp ui wait-for "PrimaryExportButton" -a $AppPid -t 4000 }
Test-UI "PrimaryExportButton enabled after capture" { winapp ui wait-for "PrimaryExportButton" -a $AppPid -p IsEnabled --value "True" -t 5000 }
winapp ui screenshot -a $AppPid -o "screenshots/02-after-capture.png" 2>$null

# --- 4. Accessibility audit: interactive controls have AutomationId ---
$allElements = (winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json).elements
$appElements = @($allElements | Where-Object {
    $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|Slider|Segmented|SplitButton' -and
    $_.name -notmatch 'Minimize|Maximize|Close|System' -and
    $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
})
$missingId = @($appElements | Where-Object { -not $_.automationId })
if ($missingId.Count -eq 0) {
    $pass++; $results += @{ name = "Interactive controls have AutomationId"; status = "PASS" }
    Write-Host "  PASS: Interactive controls have AutomationId" -ForegroundColor Green
} else {
    $fail++
    $names = ($missingId | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ", "
    $results += @{ name = "AutomationId coverage"; status = "FAIL"; detail = "Missing: $names" }
    Write-Host "  FAIL: AutomationId coverage -- Missing: $names" -ForegroundColor Red
}

Write-Host "`nPassed: $pass | Failed: $fail"
$results | ConvertTo-Json | Out-File "test-results.json"
if ($fail -gt 0) { exit 1 } else { exit 0 }

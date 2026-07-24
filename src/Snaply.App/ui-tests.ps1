param(
    [Parameter(Mandatory)]
    [int]$AppPid,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [ValidateRange(0, 1000)]
    [int]$SoakIterations,

    [ValidateRange(0, 1000)]
    [int]$SoakCancellationInterval = 10,

    [ValidateRange(0, 1000)]
    [int]$SoakResizeInterval = 20,

    [ValidateRange(0, 1000)]
    [int]$SoakWarmupIterations = 30,

    [string]$HandleDiagnosticTool
)

$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
$diagnosticCount = 0
$artifacts = Join-Path $PSScriptRoot '..\..\artifacts\ui'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$artifacts = (Resolve-Path $artifacts).Path
$autoSaveDirectory = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::MyPictures)) 'Screenshots\Snaply'
$savesBeforeCapture = if (Test-Path -LiteralPath $autoSaveDirectory) {
    @(Get-ChildItem -LiteralPath $autoSaveDirectory -Filter 'Snaply-*.png' -File).Count
}
else {
    0
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WindowSizing
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort virtualKey;
        public ushort scanCode;
        public uint flags;
        public uint time;
        public UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput mouse;

        [FieldOffset(0)]
        public KeyboardInput keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint type;
        public InputUnion data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // SetForegroundWindow alone is refused when another process owns the foreground, which
    // is the normal state on these runners — the shell (WWAHost, SearchHost) keeps taking
    // it. Attaching our input queue to both the current foreground thread and the target's
    // lifts that restriction for the duration of the call, which is the documented way to
    // hand the foreground to a specific window.
    public static bool ForceForeground(IntPtr hWnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == hWnd)
        {
            return true;
        }

        uint ignored;
        uint foregroundThread = GetWindowThreadProcessId(foreground, out ignored);
        uint targetThread = GetWindowThreadProcessId(hWnd, out ignored);
        uint currentThread = GetCurrentThreadId();
        bool attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != currentThread
            && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(hWnd);
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    public static bool SendMouse(uint flags)
    {
        Input[] inputs =
        {
            new Input
            {
                type = 0,
                data = new InputUnion { mouse = new MouseInput { flags = flags } }
            }
        };
        return SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1;
    }

    public static bool SendEscape()
    {
        Input[] inputs =
        {
            new Input
            {
                type = 1,
                data = new InputUnion
                {
                    keyboard = new KeyboardInput { virtualKey = 0x1B }
                }
            },
            new Input
            {
                type = 1,
                data = new InputUnion
                {
                    keyboard = new KeyboardInput { virtualKey = 0x1B, flags = 0x0002 }
                }
            }
        };
        return SendInput(2, inputs, Marshal.SizeOf<Input>()) == 2;
    }
}
'@

function Get-AppWindow {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $captureCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'CaptureButton')
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
    foreach ($candidate in $windows) {
        $capture = $candidate.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $captureCondition)
        if ($capture) {
            return $candidate
        }
    }

    throw "No Snaply UI Automation window for process $AppPid."
}

function Get-AppElement {
    param([string]$AutomationId)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $element = (Get-AppWindow).FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if (-not $element) {
        throw "No element with AutomationId '$AutomationId'."
    }

    return $element
}

function Get-RegionSelectionWindow {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $cancelCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'RegionCancelButton')
    $windows = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    $selectionWindows = [System.Collections.Generic.List[object]]::new()
    foreach ($window in $windows) {
        if ($window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $cancelCondition)) {
            $selectionWindows.Add($window)
        }
    }

    $focusCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty,
        $true)
    foreach ($window in $selectionWindows) {
        if ($window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $focusCondition)) {
            return $window
        }
    }

    if ($selectionWindows.Count -gt 0) {
        return $selectionWindows[0]
    }

    throw 'No region selection window appeared.'
}

# Synthetic mouse and keyboard input is delivered to the foreground window, but
# BeginSelection returns as soon as it has called Activate(), so UI Automation can see
# the overlay before it can accept input. Anything driving the overlay with SendInput or
# SetCursorPos has to wait for it to actually reach the foreground first.
function Wait-RegionOverlayForeground {
    $mainHandle = [IntPtr](Get-AppWindow).Current.NativeWindowHandle
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $foreground = [WindowSizing]::GetForegroundWindow()
        $foregroundProcess = [uint32]0
        $null = [WindowSizing]::GetWindowThreadProcessId(
            $foreground,
            [ref]$foregroundProcess)
        if ($foreground -ne [IntPtr]::Zero -and
            $foreground -ne $mainHandle -and
            $foregroundProcess -eq $AppPid) {
            return $foreground
        }

        # The shell keeps grabbing the foreground on these runners, and the app cannot
        # activate over another process that holds it. Hand it to the overlay explicitly
        # rather than waiting for a window that will never come forward on its own.
        try {
            $overlayHandle = [IntPtr](Get-RegionSelectionWindow).Current.NativeWindowHandle
            $null = [WindowSizing]::ForceForeground($overlayHandle)
        }
        catch {
            # Still on its way up; keep polling until the deadline.
        }

        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'Region overlay did not reach the foreground.'
}

# Reaching the foreground is necessary but not sufficient: the press still has to land on
# the overlay's content. Hit-test the press point through UI Automation until it resolves
# to the app, so PointerPressed is guaranteed to see it rather than firing into a window
# that is foreground but not yet hit-testable.
function Wait-PointerTarget {
    param([int]$X, [int]$Y)

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $element = [System.Windows.Automation.AutomationElement]::FromPoint(
            [System.Windows.Point]::new($X, $Y))
        if ($element -and $element.Current.ProcessId -eq $AppPid) {
            return
        }

        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "No window of the app is hit-testable at $X,$Y."
}

function Wait-ProcessElement {
    param(
        [string]$AutomationId,
        [int]$Timeout = 5000
    )

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $AppPid),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId))
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $element = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($element) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "'$AutomationId' did not appear within ${Timeout}ms."
}

function Wait-AppElement {
    param(
        [string]$AutomationId,
        [ValidateSet('Exists', 'IsEnabled', 'IsOffscreen')]
        [string]$Property = 'Exists',
        [bool]$Value = $true,
        [int]$Timeout = 3000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    $lastError = $null
    do {
        try {
            $element = Get-AppElement $AutomationId
            $actual = switch ($Property) {
                'Exists' { $true }
                'IsEnabled' { $element.Current.IsEnabled }
                'IsOffscreen' { $element.Current.IsOffscreen }
            }
            if ($actual -eq $Value) {
                return $element
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "'$AutomationId' did not reach $Property=$Value within ${Timeout}ms. $lastError"
}

function Get-CapturePickerCancelButtons {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cancelCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'CancelButton')
    $buttons = [System.Collections.Generic.List[object]]::new()
    $windows = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($window in $windows) {
        if ($window.Current.Name -notmatch 'Snaply') {
            continue
        }

        $button = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $cancelCondition)
        if ($button) {
            $buttons.Add($button)
        }
    }

    return $buttons
}

function Close-CapturePickers {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $before = @(Get-CapturePickerCancelButtons)
        if ($before.Count -eq 0) {
            return
        }

        $invokeError = $null
        try {
            $before[0].GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        }
        catch {
            $invokeError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 200
        $after = @(Get-CapturePickerCancelButtons)
        if ($after.Count -ge $before.Count -and $invokeError) {
            throw $invokeError
        }
    }

    throw 'Stale GraphicsCapturePicker windows did not close.'
}

# A failure message only says which element never showed up. Snapshot the process's
# actual window tree first, so the report distinguishes "the window was never created"
# from "it exists but automation cannot see it". Capped, and never allowed to mask the
# real failure.
function Write-FailureDiagnostic {
    param([string]$Name)

    if ($script:diagnosticCount -ge 2) {
        return
    }

    $script:diagnosticCount++
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("=== $Name ===")
    try {
        $lines.Add("Responding: $((Get-Process -Id $AppPid).Responding)")
        # Synthetic input lands on the foreground window, so record who actually had it.
        $foreground = [WindowSizing]::GetForegroundWindow()
        $foregroundProcess = [uint32]0
        $null = [WindowSizing]::GetWindowThreadProcessId(
            $foreground,
            [ref]$foregroundProcess)
        $owner = try {
            (Get-Process -Id $foregroundProcess -ErrorAction Stop).ProcessName
        }
        catch {
            'unknown'
        }
        $lines.Add(
            "Foreground: hwnd=$foreground pid=$foregroundProcess ($owner)" +
            " app pid=$AppPid")
        # When something outside the app holds the foreground, synthetic input never
        # reaches the overlay. Name every top-level window so the culprit is identifiable.
        $desktop = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        $lines.Add("Desktop top-level windows: $($desktop.Count)")
        foreach ($window in $desktop) {
            $lines.Add(
                "  pid=$($window.Current.ProcessId)" +
                " class=$($window.Current.ClassName)" +
                " name='$($window.Current.Name)'")
        }
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $processCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $AppPid)
        $windows = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $processCondition)
        $lines.Add("Top-level windows: $($windows.Count)")
        foreach ($window in $windows) {
            $lines.Add(
                "  class=$($window.Current.ClassName)" +
                " name='$($window.Current.Name)'" +
                " bounds=$($window.Current.BoundingRectangle)" +
                " offscreen=$($window.Current.IsOffscreen)")
            $descendants = $window.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($element in $descendants) {
                if (-not $element.Current.AutomationId) {
                    continue
                }

                $lines.Add(
                    "    $($element.Current.AutomationId)" +
                    " type=$($element.Current.ControlType.ProgrammaticName)" +
                    " offscreen=$($element.Current.IsOffscreen)" +
                    " enabled=$($element.Current.IsEnabled)")
            }
        }
    }
    catch {
        $lines.Add("Diagnostic capture failed: $($_.Exception.Message)")
    }

    Add-Content -LiteralPath (Join-Path $artifacts 'diagnostics.txt') -Value $lines
}

function Test-Ui {
    param([string]$Name, [scriptblock]$Action)

    try {
        & $Action
        if ($LASTEXITCODE -notin @(0, $null)) {
            throw "Exit code $LASTEXITCODE"
        }

        $results.Add([pscustomobject]@{ name = $Name; status = 'PASS' })
    }
    catch {
        Write-FailureDiagnostic $Name
        [WindowSizing]::SendMouse(0x0004) | Out-Null
        [WindowSizing]::SendEscape() | Out-Null
        Start-Sleep -Milliseconds 100
        $results.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message })
    }
}

function Invoke-CaptureMode {
    param([string]$AutomationId)

    $lastError = $null
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            $capture = Wait-AppElement CaptureButton IsEnabled $true 5000
            $capture.SetFocus()
            $expand = $capture.GetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($expand.Current.ExpandCollapseState -eq
                [System.Windows.Automation.ExpandCollapseState]::Expanded) {
                $expand.Collapse()
            }

            $expand.Expand()
            $item = Wait-ProcessElement $AutomationId 2000
            $item.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            # The flyout item only selects the mode — the pill body is what runs the
            # capture (MainPage.xaml.cs: RegionCaptureItem_Click -> SelectMode, capture
            # happens in CaptureButton_Click). Invoking the item alone starts nothing.
            $capture = Wait-AppElement CaptureButton IsEnabled $true 5000
            $capture.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            return
        }
        catch {
            $lastError = $_.Exception.Message
            [WindowSizing]::SendEscape() | Out-Null
            Start-Sleep -Milliseconds 100
        }
    }

    throw "Could not invoke '$AutomationId'. $lastError"
}

function Invoke-PrimaryCapture {
    $capture = Get-AppElement 'CaptureButton'
    $capture.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Wait-CaptureComplete {
    param([int]$Timeout = 20000)

    Wait-AppElement CaptureButton IsEnabled $true $Timeout | Out-Null
    Wait-AppElement PreviewImage IsOffscreen $false 3000 | Out-Null
}

foreach ($id in @(
    'CaptureButton',
    'OpenFolderButton'
)) {
    Test-Ui "$id exists" {
        Wait-AppElement $id | Out-Null
    }
}

Test-Ui 'Open Folder enabled before capture' {
    Wait-AppElement OpenFolderButton IsEnabled $true | Out-Null
}

Test-Ui 'Narrow window keeps capture reachable' {
    $window = Get-AppWindow
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    if (-not [WindowSizing]::SetWindowPos(
            $handle, [IntPtr]::Zero, 80, 80, 520, 480, 0x0014)) {
        throw 'Narrow window resize failed.'
    }

    Wait-AppElement CaptureButton IsOffscreen $false | Out-Null
    if (-not [WindowSizing]::SetWindowPos(
            $handle, [IntPtr]::Zero, 80, 80, 1100, 720, 0x0014)) {
        throw 'Window restore failed.'
    }
}

Test-Ui 'Region cancellation recovers' {
    Invoke-CaptureMode RegionCaptureItem
    (Wait-ProcessElement RegionCancelButton).
        GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).
        Invoke()
    Wait-AppElement CaptureButton IsEnabled $true 3000 | Out-Null
}

Test-Ui 'Region capture completes' {
    Invoke-CaptureMode RegionCaptureItem
    $null = Wait-ProcessElement RegionCancelButton
    $null = Wait-RegionOverlayForeground
    $overlay = Get-RegionSelectionWindow
    $bounds = $overlay.Current.BoundingRectangle
    $startX = [int]($bounds.Left + [Math]::Min(240, $bounds.Width / 4))
    $startY = [int]($bounds.Top + [Math]::Min(200, $bounds.Height / 4))
    $endX = [int][Math]::Min($bounds.Right - 40, $startX + 320)
    $endY = [int][Math]::Min($bounds.Bottom - 40, $startY + 240)
    if ($endX - $startX -lt 2 -or $endY - $startY -lt 2) {
        throw 'Region overlay is too small for the drag journey.'
    }

    Wait-PointerTarget $startX $startY
    if (-not [WindowSizing]::SetCursorPos($startX, $startY)) {
        throw 'Could not position the region pointer.'
    }

    if (-not [WindowSizing]::SendMouse(0x0002)) {
        throw 'Could not press the region pointer.'
    }

    Start-Sleep -Milliseconds 100
    # Step the cursor in absolute coordinates. Relative SendInput moves are scaled by the
    # pointer speed and "enhance pointer precision" settings, so the drag landed somewhere
    # other than the target and the selection never closed — the overlay was still up when
    # the assertion timed out, and arm64 (different pointer defaults) failed far more often.
    for ($step = 1; $step -le 10; $step++) {
        $x = [int]($startX + (($endX - $startX) * $step / 10))
        $y = [int]($startY + (($endY - $startY) * $step / 10))
        if (-not [WindowSizing]::SetCursorPos($x, $y)) {
            throw 'Could not drag the region pointer.'
        }

        Start-Sleep -Milliseconds 30
    }

    if (-not [WindowSizing]::SendMouse(0x0004)) {
        throw 'Could not release the region pointer.'
    }

    Wait-AppElement CaptureButton IsEnabled $true 20000 | Out-Null
    Wait-AppElement PreviewImage IsOffscreen $false 3000 | Out-Null
}

Test-Ui 'Window picker cancellation recovers' {
    Close-CapturePickers
    Invoke-CaptureMode WindowCaptureItem
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $cancel = @(Get-CapturePickerCancelButtons) | Select-Object -First 1
        if (-not $cancel) {
            Start-Sleep -Milliseconds 100
        }
    } while (-not $cancel -and [DateTime]::UtcNow -lt $deadline)
    if (-not $cancel) {
        throw 'GraphicsCapturePicker did not appear.'
    }

    $invokeError = $null
    try {
        $cancel.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    catch {
        $invokeError = $_.Exception.Message
    }

    try {
        Wait-AppElement CaptureButton IsEnabled $true 5000 | Out-Null
    }
    catch {
        if ($invokeError) {
            throw "$invokeError $($_.Exception.Message)"
        }

        throw
    }
}

Test-Ui 'Window capture completes' {
    $started = [DateTime]::Now
    $target = Start-Process notepad.exe -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $target = Get-Process notepad -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.StartTime -ge $started -and
                    $_.MainWindowHandle -ne [IntPtr]::Zero -and
                    -not [string]::IsNullOrWhiteSpace($_.MainWindowTitle)
                } |
                Sort-Object StartTime -Descending |
                Select-Object -First 1
            if (-not $target) {
                Start-Sleep -Milliseconds 100
            }
        } while (-not $target -and [DateTime]::UtcNow -lt $deadline)
        if (-not $target) {
            throw 'The window capture target did not start.'
        }

        Close-CapturePickers
        Invoke-CaptureMode WindowCaptureItem
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $itemCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $items = $root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $itemCondition)
            $item = @($items | Where-Object {
                $_.Current.Name -like "*$($target.MainWindowTitle)*"
            }) | Select-Object -First 1
            if (-not $item) {
                Start-Sleep -Milliseconds 100
            }
        } while (-not $item -and [DateTime]::UtcNow -lt $deadline)
        if (-not $item) {
            throw "The picker did not list '$($target.MainWindowTitle)'."
        }

        $item.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $acceptCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'AcceptButton')
        $accept = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $acceptCondition)
        if (-not $accept) {
            throw 'The picker accept button is missing.'
        }

        $accept.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-CaptureComplete
    }
    finally {
        Close-CapturePickers
        Wait-AppElement CaptureButton IsEnabled $true 5000 | Out-Null
        if ($target -and -not $target.HasExited) {
            $null = $target.CloseMainWindow()
            if (-not $target.WaitForExit(5000)) {
                Stop-Process -Id $target.Id -Force
            }
        }
    }
}

Test-Ui 'Desktop capture completes' {
    Invoke-CaptureMode DesktopCaptureItem
    Wait-CaptureComplete
}
Test-Ui 'Automatic save created a PNG' {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $current = if (Test-Path -LiteralPath $autoSaveDirectory) {
            @(Get-ChildItem -LiteralPath $autoSaveDirectory -Filter 'Snaply-*.png' -File).Count
        }
        else {
            0
        }
        if ($current -gt $savesBeforeCapture) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($current -le $savesBeforeCapture) {
        throw 'No automatic save appeared.'
    }
}
Test-Ui 'Capture places a bitmap on the clipboard' {
    if (-not [System.Windows.Forms.Clipboard]::ContainsImage()) {
        throw 'Clipboard contains no image.'
    }
}
Test-Ui 'Open Folder opens the automatic-save directory' {
    (Get-AppElement 'OpenFolderButton').
        GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).
        Invoke()
    $expected = [Uri]::new($autoSaveDirectory).AbsoluteUri.TrimEnd('/')
    $shell = New-Object -ComObject Shell.Application
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    $matching = $null
    do {
        $matching = @($shell.Windows() | Where-Object {
            $_.LocationURL.TrimEnd('/') -eq $expected
        }) | Select-Object -First 1
        if (-not $matching) {
            Start-Sleep -Milliseconds 200
        }
    } while (-not $matching -and [DateTime]::UtcNow -lt $deadline)

    if (-not $matching) {
        throw 'Open Folder did not open the automatic-save directory.'
    }
    $matching.Quit()
}

Test-Ui 'Interactive controls expose UI Automation identity' {
    $inspection = winapp ui inspect -a $AppPid --interactive --json | ConvertFrom-Json
    $missing = @($inspection.windows.elements | Where-Object {
        $_.type -match 'Button|SplitButton' -and
        $_.name -notmatch 'Minimize|Maximize|Close|System|システム' -and
        (-not $_.automationId -or -not $_.name)
    })
    if ($missing.Count -ne 0) {
        # Reporting the name is useless here — a missing name is exactly what this
        # catches, so the message came out empty. Identify the element instead.
        throw (($missing | ForEach-Object {
                    "$($_.type) automationId='$($_.automationId)' name='$($_.name)'"
                }) -join '; ')
    }
}

function Invoke-RegionCancellation {
    $cleanupRequired = $true
    try {
        Invoke-CaptureMode RegionCaptureItem
        $null = Wait-RegionOverlayForeground
        if (-not [WindowSizing]::SendEscape()) {
            throw 'Escape input failed.'
        }

        Wait-AppElement CaptureButton IsEnabled $true 5000 | Out-Null
        $cleanupRequired = $false
    }
    finally {
        if ($cleanupRequired) {
            [WindowSizing]::SendEscape() | Out-Null
            Start-Sleep -Milliseconds 100
            [WindowSizing]::SendEscape() | Out-Null
        }
    }
}

function Invoke-SoakStep {
    param([int]$Iteration)

    if ($SoakCancellationInterval -gt 0 -and
        $Iteration % $SoakCancellationInterval -eq 0) {
        Invoke-RegionCancellation
    }

    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    if ($SoakCancellationInterval -gt 0 -and
        $Iteration % $SoakCancellationInterval -eq 0) {
        Invoke-CaptureMode DesktopCaptureItem
    }
    else {
        Invoke-PrimaryCapture
    }

    Wait-CaptureComplete
    $timer.Stop()

    if ($SoakResizeInterval -gt 0 -and
        $Iteration % $SoakResizeInterval -eq 0) {
        $width = if (($Iteration / $SoakResizeInterval) % 2 -eq 0) { 520 } else { 1100 }
        $height = if (($Iteration / $SoakResizeInterval) % 2 -eq 0) { 480 } else { 720 }
        Set-AppWindowSize $width $height
    }

    return $timer.Elapsed.TotalSeconds
}

function Set-AppWindowSize {
    param(
        [int]$Width,
        [int]$Height
    )

    $window = Get-AppWindow
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    if (-not [WindowSizing]::SetWindowPos(
            $handle, [IntPtr]::Zero, 80, 80, $Width, $Height, 0x0014)) {
        throw 'Window resize failed.'
    }
}

function Get-StableProcessMetrics {
    param([System.Diagnostics.Process]$Process)

    $handles = [System.Collections.Generic.List[int]]::new()
    $memory = [System.Collections.Generic.List[long]]::new()
    for ($sample = 0; $sample -lt 20; $sample++) {
        $Process.Refresh()
        $handles.Add($Process.HandleCount)
        $memory.Add($Process.PrivateMemorySize64)
        Start-Sleep -Milliseconds 100
    }

    return [pscustomobject]@{
        handles = ($handles | Measure-Object -Minimum).Minimum
        privateBytes = ($memory | Measure-Object -Minimum).Minimum
    }
}

function Write-HandleSnapshot {
    param([string]$Name)

    if (-not $HandleDiagnosticTool) {
        return
    }

    if (-not (Test-Path -LiteralPath $HandleDiagnosticTool -PathType Leaf)) {
        throw 'Handle diagnostic tool was not found.'
    }

    & $HandleDiagnosticTool -accepteula -nobanner -a -p $AppPid |
        Set-Content -Encoding utf8 (Join-Path $artifacts "handles-$Name.txt")
    if ($LASTEXITCODE -ne 0) {
        throw 'Handle diagnostic collection failed.'
    }
}

if ($SoakIterations -gt 0) {
    Test-Ui "$SoakIterations capture soak" {
        $process = Get-Process -Id $AppPid
        $durations = [System.Collections.Generic.List[double]]::new()
        $samples = [System.Collections.Generic.List[object]]::new()

        for ($iteration = 1; $iteration -le $SoakWarmupIterations; $iteration++) {
            $null = Invoke-SoakStep $iteration
        }

        if ($SoakResizeInterval -gt 0) {
            Set-AppWindowSize 520 480
            Start-Sleep -Milliseconds 250
            if ($SoakCancellationInterval -gt 0) {
                Invoke-RegionCancellation
                Invoke-CaptureMode DesktopCaptureItem
                Wait-CaptureComplete
            }
            else {
                $null = Invoke-SoakStep 1
            }
        }

        Set-AppWindowSize 1100 720
        Start-Sleep -Milliseconds 250
        $baseline = Get-StableProcessMetrics $process
        Write-HandleSnapshot baseline
        for ($iteration = 1; $iteration -le $SoakIterations; $iteration++) {
            $step = $SoakWarmupIterations + $iteration
            $durations.Add((Invoke-SoakStep $step))

            if ($iteration % 10 -eq 0) {
                $process.Refresh()
                $samples.Add([pscustomobject]@{
                    iteration = $iteration
                    handles = $process.HandleCount
                    privateBytes = $process.PrivateMemorySize64
                })
            }
        }

        Set-AppWindowSize 1100 720
        Start-Sleep -Milliseconds 250
        $final = Get-StableProcessMetrics $process
        Write-HandleSnapshot final
        $ordered = $durations | Sort-Object
        $p95Index = [Math]::Max(0, [Math]::Ceiling($ordered.Count * 0.95) - 1)
        $p95 = $ordered[$p95Index]
        $limit = if ($Architecture -eq 'arm64') { 3.5 } else { 2.5 }

        [pscustomobject]@{
            iterations = $SoakIterations
            warmupIterations = $SoakWarmupIterations
            p95Seconds = $p95
            initialHandles = $baseline.handles
            finalHandles = $final.handles
            initialPrivateBytes = $baseline.privateBytes
            finalPrivateBytes = $final.privateBytes
            samples = $samples
        } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $artifacts 'soak-results.json')

        if ($final.handles -gt $baseline.handles) {
            throw "Handle count grew from $($baseline.handles) to $($final.handles)."
        }

        $memoryLimit = [long][Math]::Ceiling($baseline.privateBytes * 1.10)
        if ($final.privateBytes -gt $memoryLimit) {
            throw "Private memory grew by more than 10%."
        }

        if ($p95 -gt $limit) {
            throw "Capture p95 was $([Math]::Round($p95, 3))s; limit is ${limit}s."
        }
    }
}

$results | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 (Join-Path $artifacts 'test-results.json')
$failed = @($results | Where-Object status -eq 'FAIL')
Write-Host "Passed: $($results.Count - $failed.Count) | Failed: $($failed.Count)"
$failed | ForEach-Object { Write-Host "FAIL: $($_.name) - $($_.detail)" }
if ($failed.Count -ne 0) {
    exit 1
}

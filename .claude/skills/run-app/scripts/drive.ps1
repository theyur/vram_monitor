# Drive the WPF app through UI Automation, which is how you interact with a native
# WPF window from a script -- there is no CDP/Playwright equivalent here. WPF publishes
# its visual tree to UIA automatically, so no instrumentation is needed in the app.
#
#   .\drive.ps1 buttons                      # list clickable buttons by name
#   .\drive.ps1 click 'Settings…'            # invoke a button (mind the … -- see notes)
#   .\drive.ps1 rows                         # count the selectable consumer rows
#   .\drive.ps1 select 1                     # select a row by index (isolates it on the chart)
#   .\drive.ps1 dialogs                      # list open modal dialogs
#   .\drive.ps1 dialog-click 'VRAM Monitor settings' 'Cancel'
param(
    [Parameter(Mandatory = $true)][string]$Action,
    [string]$Arg1,
    [string]$Arg2,
    [string]$ProcessName = 'VramMonitor'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
if (-not $proc) { throw "$ProcessName is not running." }
if ($proc.MainWindowHandle -eq 0) { throw "$ProcessName has no main window." }
$main = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function ByName([string]$name) {
    New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
}
function Descendants($root, $condition) {
    $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Element($element) {
    $element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# A modal WPF dialog is NOT a child of the desktop root, so enumerating the desktop's
# children by process id will not find it. It lives under the owning window instead.
function Find-Dialog([string]$title) {
    $d = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (ByName $title))
    if (-not $d) {
        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $d = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Subtree, (ByName $title))
    }
    return $d
}

switch ($Action) {
    'buttons' {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $found = Descendants $main $cond
        "buttons: $($found.Count)"
        $seen = @{}
        foreach ($b in $found) {
            $n = $b.Current.Name
            if ($n -and -not $seen.ContainsKey($n)) { $seen[$n] = $true; "  '$n'" }
        }
    }
    'click' {
        if (-not $Arg1) { throw "click needs a button name; run 'buttons' to see them." }
        $b = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (ByName $Arg1))
        if (-not $b) { throw "no element named '$Arg1'. Run 'buttons' -- names use the … character, not three dots." }
        Invoke-Element $b
        "clicked '$Arg1'"
    }
    'rows' {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)
        $found = Descendants $main $cond
        "selectable rows: $($found.Count) (aggressive consumers first, then other consumers)"
    }
    'select' {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)
        $found = Descendants $main $cond
        $i = [int]$Arg1
        if ($i -lt 0 -or $i -ge $found.Count) { throw "row index $i out of range (0..$($found.Count - 1))." }
        $found.Item($i).GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        "selected row $i"
    }
    'dialogs' {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)
        $found = Descendants $main $cond
        if ($found.Count -eq 0) { 'no modal dialogs open' }
        foreach ($w in $found) { "dialog: '$($w.Current.Name)'" }
    }
    'dialog-click' {
        if (-not $Arg1 -or -not $Arg2) { throw "dialog-click needs <dialog title> <button name>." }
        $d = Find-Dialog $Arg1
        if (-not $d) { throw "dialog '$Arg1' not found." }
        $b = $d.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (ByName $Arg2))
        if (-not $b) { throw "button '$Arg2' not found in '$Arg1'." }
        Invoke-Element $b
        Start-Sleep -Milliseconds 1500
        if (Find-Dialog $Arg1) { "clicked '$Arg2' but '$Arg1' is still open" } else { "clicked '$Arg2'; '$Arg1' closed" }
    }
    default { throw "unknown action '$Action'. Use: buttons | click | rows | select | dialogs | dialog-click" }
}

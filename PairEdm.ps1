<#
.SYNOPSIS
    Pairs (bonds) a Bluetooth EDM by its MAC address without needing it to be
    discoverable, then reports the SPP COM port Windows creates.

.DESCRIPTION
    Windows' "Add device" UI only lists *discoverable* devices, so a silent EDM
    can't be paired through the GUI. This bonds directly to the known address via
    the WinRT pairing API using "Just Works" (ConfirmOnly, no PIN) — the desktop
    equivalent of Android's createBond(by MAC). Once bonded, Windows exposes the
    outgoing Serial Port Profile COM port, which the Dyno Command PC app's
    "Find EDM" button (or this script) can then locate.

.NOTES
    Run in **Windows PowerShell 5.1** (powershell.exe), NOT PowerShell 7 (pwsh) —
    the WinRT projection used here needs Windows PowerShell. No admin required.
    The EDM must be powered on, connectable, and not already connected to
    anything else (it is single-client).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\PairEdm.ps1
    powershell -ExecutionPolicy Bypass -File .\PairEdm.ps1 -Mac 0012F304962C
#>

param(
    [string]$Mac = "0012F304962C"
)

$ErrorActionPreference = "Stop"

# --- Load WinRT projections + the AsTask bridge (Windows PowerShell 5.1) ---
Add-Type -AssemblyName System.Runtime.WindowsRuntime -ErrorAction SilentlyContinue | Out-Null
[Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType = WindowsRuntime] | Out-Null
[Windows.Devices.Enumeration.DeviceInformationCustomPairing, Windows.Devices.Enumeration, ContentType = WindowsRuntime] | Out-Null
[Windows.Devices.Enumeration.DevicePairingKinds, Windows.Devices.Enumeration, ContentType = WindowsRuntime] | Out-Null
[Windows.Devices.Enumeration.DevicePairingResult, Windows.Devices.Enumeration, ContentType = WindowsRuntime] | Out-Null
[Windows.Foundation.IAsyncOperation`1, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null

# Helper: synchronously await a WinRT IAsyncOperation<T>
function Await($op, $resultType) {
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } | Select-Object -First 1
    $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($op))
    $task.Wait(-1) | Out-Null
    $task.Result
}

$macClean = ($Mac -replace '[^0-9A-Fa-f]', '').ToUpper()
if ($macClean.Length -ne 12) { throw "MAC must be 12 hex digits, e.g. 0012F304962C. Got '$Mac'." }
$addr = [Convert]::ToUInt64($macClean, 16)
$pretty = ($macClean -split '(..)' -ne '' ) -join ':'

Write-Host "Target EDM : $pretty  (0x$macClean)" -ForegroundColor Cyan
Write-Host "Resolving device by address..." -ForegroundColor Cyan

$device = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothDevice])
if ($null -eq $device) {
    throw "Could not reach $pretty. Make sure the EDM is powered on, in range, connectable, and not already connected to the PC or another device."
}

$info = $device.DeviceInformation
Write-Host ("Found      : {0}" -f $info.Name)

if ($info.Pairing.IsPaired) {
    Write-Host "Already paired." -ForegroundColor Green
}
else {
    if (-not $info.Pairing.CanPair) {
        throw "Device reports it cannot be paired right now (often: already connected elsewhere, or not connectable)."
    }

    $custom = $info.Pairing.Custom

    # Accept the Just-Works ceremony with no prompt.
    $handler = [Windows.Foundation.TypedEventHandler[Windows.Devices.Enumeration.DeviceInformationCustomPairing, Windows.Devices.Enumeration.DevicePairingRequestedEventArgs]] {
        param($s, $e)
        Write-Host ("  Pairing request (kind: {0}) -> accepting" -f $e.PairingKind)
        if ($e.PairingKind -eq [Windows.Devices.Enumeration.DevicePairingKinds]::ConfirmOnly) {
            $e.Accept()
        }
        else {
            $e.Accept()   # also accept ProvidePin/Confirm variants for robustness
        }
    }
    # add_ returns an EventRegistrationToken; remove_ needs that token (not the handler).
    $token = $custom.add_PairingRequested($handler)

    Write-Host "Pairing (Just Works / ConfirmOnly)..." -ForegroundColor Cyan
    $result = Await ($custom.PairAsync([Windows.Devices.Enumeration.DevicePairingKinds]::ConfirmOnly)) ([Windows.Devices.Enumeration.DevicePairingResult])

    $okStatuses = @('Paired', 'AlreadyPaired')
    Write-Host ("Pairing status: {0}" -f $result.Status) -ForegroundColor $(if ($okStatuses -contains [string]$result.Status) { 'Green' } else { 'Yellow' })

    try { $custom.remove_PairingRequested($token) } catch { }

    if ($okStatuses -notcontains [string]$result.Status) {
        throw "Pairing did not complete (status: $($result.Status)). Ensure the EDM is connectable and not connected elsewhere, power-cycle it, then re-run."
    }
}

# --- Find the outgoing SPP COM port for this MAC (may take a moment to appear) ---
Write-Host "Looking for the SPP COM port..." -ForegroundColor Cyan
$port = $null
for ($i = 0; $i -lt 10 -and -not $port; $i++) {
    $match = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '\(COM\d+\)' -and $_.PNPDeviceID -match $macClean } |
        Select-Object -First 1
    if ($match -and ($match.Name -match '\((COM\d+)\)')) { $port = $Matches[1] }
    if (-not $port) { Start-Sleep -Milliseconds 600 }
}

if ($port) {
    Write-Host ""
    Write-Host "SUCCESS - EDM paired and available on $port" -ForegroundColor Green
    Write-Host "In the Dyno Command PC app: choose Serial / Bluetooth, select $port, Connect." -ForegroundColor Green
    Write-Host "(or just click 'Find EDM' in the app)" -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "Paired, but no outgoing COM port found yet." -ForegroundColor Yellow
    Write-Host "Open  control bthprops.cpl  -> COM Ports -> Add -> Outgoing -> select the EDM -> SPP," -ForegroundColor Yellow
    Write-Host "then use that COM port in the app." -ForegroundColor Yellow
}

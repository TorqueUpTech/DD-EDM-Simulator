# Add the required Windows Runtime type assemblies
[void][Windows.Security.Cryptography.CryptographicBuffer, Windows.Security.Cryptography, ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]

# Convert hex to an unsigned 64-bit integer
$MacAddressInt = [Convert]::ToUInt64("0012F304962C", 16)

# Use the cleaner, direct ID lookup method
$DeviceTask = [Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($MacAddressInt)

# Wait for the async task to finish safely
while ($DeviceTask.Status -eq 'Started') { 
    Start-Sleep -Milliseconds 100 
}

$Device = $DeviceTask.GetResults()

# Output the results
if ($Device) { 
    Write-Host "Successfully targeted device: $($Device.Name)" -ForegroundColor Green
    Write-Host "Connection Status: $($Device.ConnectionStatus)" -ForegroundColor Cyan
} else {
    Write-Host "Device could not be reached or is out of range." -ForegroundColor Red
}

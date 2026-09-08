Add-Type -Path "src\WinARD.Remote.Protocol\bin\Release\net8.0-windows10.0.19041.0\WinARD.Remote.Protocol.dll"
Add-Type -Path "tools\WinARD.ProtocolProbe\bin\Release\net8.0-windows10.0.19041.0\WinARD.ProtocolProbe.dll"

$redBytes = [Convert]::FromHexString("41413680806081A17DE0000000000000000685F76D")
$greenBytes = [Convert]::FromHexString("41413680806082FD04E0000000000000000BF4136D")
$blueBytes = [Convert]::FromHexString("4141368080608073F6E00000000000000001CFDB6D")
$whiteBytes = [Convert]::FromHexString("310836807F006D")
$blackBytes = [Convert]::FromHexString("418B68006081B4")

function Parse-Sample([string]$name, [byte[]]$bytes) {
    Write-Host "`n=== Parsing $name ($($bytes.Length) bytes) ===" -ForegroundColor Cyan
    $reader = [WinARD.ProtocolProbe.EncodingResearch.MvsBitReader]::new($bytes)
    
    # Let's inspect bit by bit or byte by byte
    $sb = New-Object System.Text.StringBuilder
    while ($reader.BitsRemaining -gt 0) {
        $chunk = [Math]::Min(8, $reader.BitsRemaining)
        $val = $reader.ReadBits($chunk)
        $hex = $val.ToString("X2")
        $bin = [Convert]::ToString($val, 2).PadLeft($chunk, '0')
        [void]$sb.Append("$bin ($hex) ")
    }
    Write-Host $sb.ToString()
}

Parse-Sample "Red" $redBytes
Parse-Sample "Green" $greenBytes
Parse-Sample "Blue" $blueBytes
Parse-Sample "White" $whiteBytes
Parse-Sample "Black" $blackBytes

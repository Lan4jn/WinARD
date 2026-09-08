$samples = @(
    @{ Name = "Red";   Hex = "41 41 36 80 80 60 81 A1 7D E0 00 00 00 00 00 00 00 06 85 F7 6D" },
    @{ Name = "Green"; Hex = "41 41 36 80 80 60 82 FD 04 E0 00 00 00 00 00 00 00 0B F4 13 6D" },
    @{ Name = "Blue";  Hex = "41 41 36 80 80 60 80 73 F6 E0 00 00 00 00 00 00 00 01 CF DB 6D" },
    @{ Name = "White"; Hex = "31 08 36 80 7F 00 6D" },
    @{ Name = "Black"; Hex = "41 8B 68 00 60 81 B4" }
)

function ToBits([string]$hexStr) {
    $bytes = $hexStr.Split(' ') | ForEach-Object { [Convert]::ToByte($_, 16) }
    $bits = ($bytes | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join ''
    return $bits
}

foreach ($s in $samples) {
    $bits = ToBits $s.Hex
    Write-Host "$($s.Name.PadRight(6)): $bits"
}

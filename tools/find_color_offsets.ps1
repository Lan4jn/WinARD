$red = [Convert]::FromHexString("41413680806081A17DE0000000000000000685F76D")
$green = [Convert]::FromHexString("41413680806082FD04E0000000000000000BF4136D")
$blue = [Convert]::FromHexString("4141368080608073F6E00000000000000001CFDB6D")

function ToBitArray([byte[]]$b) {
    $list = New-Object System.Collections.Generic.List[int]
    foreach ($byte in $b) {
        for ($i = 7; $i -ge 0; $i--) {
            $list.Add(($byte -shr $i) -band 1)
        }
    }
    return $list
}

$rBits = ToBitArray $red
$gBits = ToBitArray $green
$bBits = ToBitArray $blue

function ReadInt([System.Collections.Generic.List[int]]$bits, [int]$offset, [int]$len) {
    if ($offset + $len -gt $bits.Count) { return -1 }
    $val = 0
    for ($i = 0; $i -lt $len; $i++) {
        $val = ($val -shl 1) -bor $bits[$offset + $i]
    }
    return $val
}

Write-Host "Searching for bit offsets with monotonic correlation to Y, Cb, Cr..." -ForegroundColor Cyan

for ($len = 4; $len -le 10; $len++) {
    for ($bitPos = 48; $bitPos -lt 160; $bitPos++) {
        $rVal = ReadInt $rBits $bitPos $len
        $gVal = ReadInt $gBits $bitPos $len
        $bVal = ReadInt $bBits $bitPos $len
        if ($rVal -ge 0 -and $gVal -ge 0 -and $bVal -ge 0) {
            # Check Y correlation: Blue < Red < Green
            if ($bVal -lt $rVal -and $rVal -lt $gVal) {
                Write-Host "Y  candidate at bit $bitPos (len $len): B=$bVal, R=$rVal, G=$gVal" -ForegroundColor Yellow
            }
            # Check Cb correlation: Green < Red < Blue
            if ($gVal -lt $rVal -and $rVal -lt $bVal) {
                Write-Host "Cb candidate at bit $bitPos (len $len): G=$gVal, R=$rVal, B=$bVal" -ForegroundColor Green
            }
            # Check Cr correlation: Green < Blue < Red
            if ($gVal -lt $bVal -and $bVal -lt $rVal) {
                Write-Host "Cr candidate at bit $bitPos (len $len): G=$gVal, B=$bVal, R=$rVal" -ForegroundColor Magenta
            }
        }
    }
}

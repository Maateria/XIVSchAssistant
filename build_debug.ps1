# Build script - fix curly quotes then compile
$file = "C:\Users\Utilisateur\Documents\XIVSchAssitant\Plugin.cs"

# Fix curly quotes introduced by Edit tool (E2 80 9C / E2 80 9D -> 0x22)
$bytes = [System.IO.File]::ReadAllBytes($file)
$out = New-Object System.Collections.Generic.List[byte]
$i = 0
while ($i -lt $bytes.Length) {
    if ($i + 2 -lt $bytes.Length -and $bytes[$i] -eq 0xE2 -and $bytes[$i+1] -eq 0x80 -and ($bytes[$i+2] -eq 0x9C -or $bytes[$i+2] -eq 0x9D)) {
        $out.Add(0x22)
        $i += 3
    } else {
        $out.Add($bytes[$i])
        $i++
    }
}
[System.IO.File]::WriteAllBytes($file, $out.ToArray())
Write-Output "Curly-quote fix done."

# Build Debug
& "C:\Users\Utilisateur\.dotnet\dotnet.exe" build "C:\Users\Utilisateur\Documents\XIVSchAssitant\XIVSchAssitant.csproj" -c Debug --no-incremental 2>&1

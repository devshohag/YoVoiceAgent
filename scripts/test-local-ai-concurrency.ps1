param(
    [Parameter(Mandatory = $true)][string]$FirstWav,
    [Parameter(Mandatory = $true)][string]$SecondWav,
    [string]$BaseUrl = "http://localhost:8090"
)

$ErrorActionPreference = "Stop"

foreach ($path in @($FirstWav, $SecondWav)) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Audio file was not found: $path"
    }
}

$jobs = @($FirstWav, $SecondWav) | ForEach-Object {
    $audioPath = (Resolve-Path $_).Path
    Start-Job -ArgumentList $BaseUrl, $audioPath -ScriptBlock {
        param($url, $path)
        $started = Get-Date
        $result = Invoke-RestMethod -Method Post `
            -Uri "$url/v1/audio/transcriptions" `
            -Form @{ file = Get-Item $path; language = "auto" }
        [PSCustomObject]@{
            File = [IO.Path]::GetFileName($path)
            Language = $result.language
            Transcript = $result.text
            ElapsedSeconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 2)
        }
    }
}

try {
    $jobs | Wait-Job | Receive-Job | Format-Table -AutoSize -Wrap
}
finally {
    $jobs | Remove-Job -Force
}

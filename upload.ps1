Set-Location "$PSScriptRoot"
$ErrorActionPreference = "Stop"

if (Test-Path '.\clowd_secrets.json') {
    $secrets = Get-Content '.\clowd_secrets.json' | Out-String | ConvertFrom-Json
} elseif (Test-Path '..\clowd_secrets.json') {
    $secrets = Get-Content '..\clowd_secrets.json' | Out-String | ConvertFrom-Json
} else {
    throw "Unable to find clowd_secrets.json"
}

Write-Host "Upload latest releases" -ForegroundColor Magenta
& $PSScriptRoot\modules\Clowd.Squirrel\build\publish\SyncReleases.exe -r "$PSScriptRoot\releases\" `
-p b2 --bucketId $secrets.b2bucket --b2keyid $secrets.b2keyid `
--b2key $secrets.b2key --upload
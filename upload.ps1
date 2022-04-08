Set-Location "$PSScriptRoot"
$ErrorActionPreference = "Stop"
$LocalProjectMode = Test-Path -Path "$PSScriptRoot\.usingproj" -PathType Leaf

# build packaging tools only if in local mode
if ($LocalProjectMode) {
    Write-Host "Squirrel (local mode)" -ForegroundColor Magenta
    Set-Alias Squirrel ("$PSScriptRoot\..\Clowd.Squirrel\build\publish\Squirrel.exe");
} else {
    $projdeps = dotnet list "$PSScriptRoot\src\Clowd\Clowd.csproj" package
    $projstring = $projdeps -join "`r`n"
    $squirrelVersion = $projstring -match "(?m)Clowd\.Squirrel.*\s(\d\.\d\.\d.*?)$"
    $squirrelVersion = $Matches[1].Trim()
    Write-Host "Using Squirrel '$squirrelVersion' from local NuGet cache" -ForegroundColor Magenta
    Set-Alias Squirrel ($env:USERPROFILE + "\.nuget\packages\clowd.squirrel\$squirrelVersion\tools\Squirrel.exe");
}
Set-Location $PSScriptRoot

if (Test-Path '.\clowd_secrets.json') {
    $secrets = Get-Content '.\clowd_secrets.json' | Out-String | ConvertFrom-Json
} elseif (Test-Path '..\clowd_secrets.json') {
    $secrets = Get-Content '..\clowd_secrets.json' | Out-String | ConvertFrom-Json
} else {
    throw "Unable to find clowd_secrets.json"
}

Write-Host "Upload latest releases" -ForegroundColor Magenta
Squirrel b2-up `
-r "$PSScriptRoot\releases" `
--b2BucketId $secrets.b2bucket `
--b2keyid $secrets.b2keyid `
--b2key $secrets.b2key
param (
    [Parameter(Mandatory=$true)][string]$mode,
    [string]$keyId,
    [string]$keySecret,
    [string]$bucket = "clowd-releases",
    [string]$channel = "experimental",
    [switch]$skipSquirrel = $false
)

# Basic Setup
$PSVersionTable.PSVersion
Set-Location $PSScriptRoot
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue" # progress bar in powershell is slow af
$LocalProjectMode = Test-Path -Path "$PSScriptRoot\.usingproj" -PathType Leaf


# Get msbuild location
$MSBuildPath = (&"${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe) | Out-String
$MSBuildPath = $MSBuildPath.Trim();


# calculate Clowd version
Write-Host "Clowd Version (nbgv)" -ForegroundColor Magenta
$gitVerJson = (&nbgv get-version -f json) | ConvertFrom-Json
$version = $gitVerJson.SimpleVersion
Write-Host $version


if ($mode -eq "compile") {

    # Ensure a clean state by removing build/package folders
    $Folders = @("$PSScriptRoot\publish", "$PSScriptRoot\bin", "$PSScriptRoot\releases")
    foreach ($Folder in $Folders) {
        if (Test-Path $Folder) {
            Remove-Item -path "$Folder" -Recurse -Force
        }
    }


    # publish clowd main project
    Write-Host "Build Clowd.csproj" -ForegroundColor Magenta
    dotnet publish "$PSScriptRoot\src\Clowd\Clowd.csproj" -c Release -r win-x64 --no-self-contained `-o "$PSScriptRoot\publish"


    # build clowd native and copy to publish directory
    Write-Host "Build Clowd.WinNative.vcxproj" -ForegroundColor Magenta
    &$MSBuildPath "$PSScriptRoot\src\Clowd.WinNative\Clowd.WinNative.vcxproj" `
    /v:minimal `
    /p:Configuration=Release `
    /p:Platform=x64 `
    /p:SolutionDir=$PSScriptRoot\
    Copy-Item ".\bin\Release\Clowd.WinNative.dll" -Destination "$PSScriptRoot\publish"


    # build obs-express only if we are in local mode, otherwise download
    $HasObsBuildFolder = Test-Path -Path "$PSScriptRoot\..\obs-express"
    if ($LocalProjectMode -And $HasObsBuildFolder) {
        Write-Host "Build obs-express (local mode)" -ForegroundColor Magenta
        Set-Location "$PSScriptRoot\..\obs-express"
        &npm install
        &npm run build
        Copy-Item "bin" -Destination "$PSScriptRoot\publish\obs-express" -Recurse
    } else {
        Write-Host "Downloading obs-express from GitHub" -ForegroundColor Magenta
        Invoke-WebRequest "https://github.com/clowd/obs-express/releases/latest/download/obs-express.zip" -OutFile "$PSScriptRoot\bin\obs-express.zip"
        Write-Host "Extracting obs-express archive" -ForegroundColor Magenta
        Expand-Archive "$PSScriptRoot\bin\obs-express.zip" -DestinationPath "$PSScriptRoot\publish\obs-express"
    }
    Set-Location $PSScriptRoot

}


# build packaging tools only if in local mode
if ($LocalProjectMode) {
    Write-Host "Build Squirrel (local mode)" -ForegroundColor Magenta
    if ($skipSquirrel -eq $false) {
        Set-Location "$PSScriptRoot\..\Clowd.Squirrel"
        & ".\build.cmd"
    }
    Set-Alias Squirrel ("$PSScriptRoot\..\Clowd.Squirrel\build\publish\Squirrel.exe");
} else {
    $projdeps = dotnet list "$PSScriptRoot\src\Clowd\Clowd.csproj" package
    $projstring = $projdeps -join "`r`n"
    $squirrelVersion = $projstring -match "(?m)Clowd\.Squirrel.*\s(\d{1,3}\.\d{1,3}\.\d.*?)$"
    $squirrelVersion = $Matches[1].Trim()
    Write-Host "Using Squirrel '$squirrelVersion' from local NuGet cache" -ForegroundColor Magenta
    Set-Alias Squirrel ($env:USERPROFILE + "\.nuget\packages\clowd.squirrel\$squirrelVersion\tools\Squirrel.exe");
}
Set-Location $PSScriptRoot


# locate s3 credentials
if (Test-Path '.\clowd_secrets.json') {
    $secrets = Get-Content '.\clowd_secrets.json' | Out-String | ConvertFrom-Json
    $keyId = $secrets.keyId;
    $keySecret = $secrets.keySecret;
} elseif (Test-Path '..\clowd_secrets.json') {
    $secrets = Get-Content '..\clowd_secrets.json' | Out-String | ConvertFrom-Json
    $keyId = $secrets.keyId;
    $keySecret = $secrets.keySecret;
}

if ([string]::IsNullOrEmpty($keyId) -Or [string]::IsNullOrEmpty($keySecret)) {
    throw "Unable to find clowd_secrets.json, cannot create release / upload"
}


if ($mode -eq "compile" -Or $mode -eq "pack") {

    # download recent packages
    New-Item -ItemType "directory" -Path "$PSScriptRoot\releases"
    Write-Host "Download latest release" -ForegroundColor Magenta
    Squirrel s3-down `
    -r "$PSScriptRoot\releases" `
    --bucket $bucket `
    --keyId $keyId `
    --secret $keySecret `
    --endpoint "https://s3.eu-central-003.backblazeb2.com" `
    --pathPrefix $channel


    # releasify
    Write-Host "Create Nuget & Releasify Package" -ForegroundColor Magenta
    Squirrel pack `
    -f net6 `
    -r "$PSScriptRoot\releases" `
    -i="$PSScriptRoot\artwork\default-setup.ico" `
    --appIcon="$PSScriptRoot\artwork\default.ico" `
    --splashImage="$PSScriptRoot\artwork\splash.gif" `
    --packId=Clowd `
    --packVersion=$version `
    --packAuthors="Caelan Sayler" `
    --packDirectory="$PSScriptRoot\publish"

    Write-Host "Done. Run .\upload.cmd to release" -ForegroundColor Magenta

}

if ($mode -eq "upload") {

    # upload local releases to s3/b2
    Write-Host "Upload latest releases" -ForegroundColor Magenta
    Squirrel s3-up `
    -r "$PSScriptRoot\releases" `
    --bucket $bucket `
    --keyId $keyId `
    --secret $keySecret `
    --endpoint "https://s3.eu-central-003.backblazeb2.com" `
    --pathPrefix $channel `
    --keepMaxReleases 20

}
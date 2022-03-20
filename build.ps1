$PSVersionTable.PSVersion
Set-Location $PSScriptRoot
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue" # progress bar in powershell is slow af
$LocalProjectMode = Test-Path -Path "$PSScriptRoot\.usingproj" -PathType Leaf


# Ensure a clean state by removing build/package folders
$Folders = @("$PSScriptRoot\publish", "$PSScriptRoot\bin", "$PSScriptRoot\releases")
foreach ($Folder in $Folders) {
    if (Test-Path $Folder) {
        Remove-Item -path "$Folder" -Recurse -Force
    }
}


# Get msbuild location
$MSBuildPath = (&"${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe) | Out-String
$MSBuildPath = $MSBuildPath.Trim();


# calculate version
Write-Host "Calculate Clowd Version (nbgv)" -ForegroundColor Magenta
$gitVerJson = (&nbgv get-version -f json) | ConvertFrom-Json
$version = $gitVerJson.SimpleVersion
Write-Host $version


# publish clowd main project
Write-Host "Build Clowd.csproj" -ForegroundColor Magenta
&$MSBuildPath "$PSScriptRoot\src\Clowd\Clowd.csproj" `
/t:Restore,Rebuild,Publish `
/v:minimal `
/p:PublishSingleFile=False `
/p:SelfContained=False `
/p:PublishProtocol=FileSystem `
/p:Configuration=Release `
/p:Platform=x64 `
/p:PublishDir="$PSScriptRoot\publish" `
/p:RuntimeIdentifier=win-x64 `
/p:PublishReadyToRun=False `
/p:PublishTrimmed=False `
/p:SolutionDir=$PSScriptRoot\


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
    wget "https://github.com/clowd/obs-express/releases/latest/download/obs-express.zip" -OutFile "$PSScriptRoot\bin\obs-express.zip"
    Write-Host "Extracting obs-express archive" -ForegroundColor Magenta
    Expand-Archive "$PSScriptRoot\bin\obs-express.zip" -DestinationPath "$PSScriptRoot\publish\obs-express"
}
Set-Location $PSScriptRoot


# build packaging tools only if in local mode
if ($LocalProjectMode) {
    Write-Host "Build Squirrel (local mode)" -ForegroundColor Magenta
    Set-Location "$PSScriptRoot\..\Clowd.Squirrel"
    & ".\build.cmd"
    Set-Location "$PSScriptRoot"
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


# locate b2 credentials
if (Test-Path '.\clowd_secrets.json') {
    $secrets = Get-Content '.\clowd_secrets.json' | Out-String | ConvertFrom-Json
} elseif (Test-Path '..\clowd_secrets.json') {
    $secrets = Get-Content '..\clowd_secrets.json' | Out-String | ConvertFrom-Json
} else {
    throw "Unable to find clowd_secrets.json, cannot create release"
}


# download recent packages
New-Item -ItemType "directory" -Path "$PSScriptRoot\releases"
Write-Host "Download latest release" -ForegroundColor Magenta
Squirrel b2-down `
-r "$PSScriptRoot\releases" `
--b2BucketId $secrets.b2bucket `
--b2keyid $secrets.b2keyid `
--b2key $secrets.b2key


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
Set-Location "$PSScriptRoot"
$ErrorActionPreference = "Stop"

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
Write-Host "Get Clowd Version" -ForegroundColor Magenta
$gitVerJson = (&nbgv get-version -f json) | ConvertFrom-Json
$version = $gitVerJson.SimpleVersion
Write-Host $version

# publish Clowd project
Write-Host "Build Clowd" -ForegroundColor Magenta
&$MSBuildPath "$PSScriptRoot\src\Clowd\Clowd.csproj" `
/t:Restore,Rebuild,Publish `
/v:minimal `
/p:PublishSingleFile=False `
/p:SelfContained=False `
/p:PublishProtocol=FileSystem `
/p:Configuration=Release `
/p:Platform=x64 `
/p:PublishDir=$PSScriptRoot\publish\Clowd `
/p:RuntimeIdentifier=win-x64 `
/p:PublishReadyToRun=False `
/p:PublishTrimmed=False `
/p:SolutionDir=$PSScriptRoot 

# build obs-express
Write-Host "Build obs-express" -ForegroundColor Magenta
Set-Location "$PSScriptRoot\modules\obs-express"
&npm install
&npm run build
Copy-Item "bin" -Destination "$PSScriptRoot\publish\Clowd\obs-express" -Recurse

# remove un-used obs plugins that are _huge_
Write-Host "Remove obs-browser plugin" -ForegroundColor Magenta
Remove-Item "$PSScriptRoot\publish\Clowd\obs-express\lib\obs-plugins\64bit\*" -Include *cef*,*browser*,*v8*,*devtools*,*chrome*,icudtl.dat
Set-Location "$PSScriptRoot"

# build packaging tools
Write-Host "Build Packaging Tools" -ForegroundColor Magenta
Set-Location "$PSScriptRoot\modules\Clowd.Squirrel"
Copy-Item "$PSScriptRoot\default-setup.ico" "src\Update\update.ico"
& ".\build.cmd"
Set-Location "$PSScriptRoot"

# create nuget package
Write-Host "Create Nuget Package" -ForegroundColor Magenta
& $PSScriptRoot\modules\Clowd.Squirrel\build\publish\NuGet.exe pack "$PSScriptRoot\src\Clowd\Clowd.nuspec" `
-BasePath "$PSScriptRoot\publish\Clowd\" `
-OutputDirectory "$PSScriptRoot\publish" `
-Version $version

if (Test-Path '.\clowd_secrets.json') {
    $secrets = Get-Content '.\clowd_secrets.json' | Out-String | ConvertFrom-Json
} elseif (Test-Path '..\clowd_secrets.json') {
    $secrets = Get-Content '..\clowd_secrets.json' | Out-String | ConvertFrom-Json
} else {
    throw "Unable to find clowd_secrets.json"
}

# download recent packages
Write-Host "Download latest release" -ForegroundColor Magenta
& $PSScriptRoot\modules\Clowd.Squirrel\build\publish\SyncReleases.exe -r "$PSScriptRoot\releases\" `
-p b2 --bucketId $secrets.b2bucket --b2keyid $secrets.b2keyid `
--b2key $secrets.b2key

# releasify
Write-Host "Releasify Nuget Package" -ForegroundColor Magenta
& $PSScriptRoot\modules\Clowd.Squirrel\build\publish\Squirrel --releasify "$PSScriptRoot\publish\Clowd.$($version).nupkg" `
--setupIcon="$PSScriptRoot\default-setup.ico" `
--no-msi `
--splashImage="$PSScriptRoot\splash.png" `
--selfContained `
--releaseDir="$PSScriptRoot\releases"

Write-Host "Done. Run .\upload.cmd to release" -ForegroundColor Magenta
Set-Location "$PSScriptRoot"
$ErrorActionPreference = "Stop"

# Ensure a clean state by removing build/package folders
$Folders = @("$PSScriptRoot\publish", "$PSScriptRoot\bin")
foreach ($Folder in $Folders) {
    if (Test-Path $Folder) {
        Remove-Item -path "$Folder" -Recurse -Force
    }
}

# Get msbuild location
$MSBuildPath = (&"${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe) | Out-String
$MSBuildPath = $MSBuildPath.Trim();

# calculate version
$gitVerJson = (&nbgv get-version -f json) | ConvertFrom-Json
$version = $gitVerJson.SimpleVersion

# publish Clowd project
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

# build packaging tools
# powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\modules\Clowd.Squirrel\build.ps1"

# create nuget package
&$PSScriptRoot\tools\NuGet.exe pack "$PSScriptRoot\src\Clowd\Clowd.nuspec" `
-BasePath "$PSScriptRoot\publish\Clowd\" `
-OutputDirectory "$PSScriptRoot\publish" `
-Version $version

# releasify
&$PSScriptRoot\tools\Squirrel.com --releasify "$PSScriptRoot\publish\Clowd.$($version).nupkg" `
--setupIcon="$PSScriptRoot\src\Clowd\Images\default.ico" `
--no-msi `
--releaseDir="$PSScriptRoot\releases"

# # Set-Location "$PSScriptRoot\publish\Clowd"
# # &$PSScriptRoot\tools\NuGet.exe spec Clowd.dll

# # &$MSBuildPath "$PSScriptRoot\src\Clowd\Clowd.csproj" `
# # /t:Pack `
# # /p:NoBuild=true `
# # /p:IncludeBuildOutput=false `
# # /p:PackageVersion="1.3" `
# # /p:PackageOutputPath="$PSScriptRoot\publish" `
# # /p:NuspecBasePath="$PSScriptRoot\publish\Clowd" `
# # /p:NuspecFile="$PSScriptRoot\src\Clowd\Clowd.nuspec"



# &$PSScriptRoot\tools\NuGet.exe pack "$PSScriptRoot\src\Clowd\Clowd.nuspec" `
# -BasePath "$PSScriptRoot\publish\Clowd\" `
# -OutputDirectory "$PSScriptRoot\publish" `
# -Version $Ver

# &$PSScriptRoot\tools\Squirrel.com --releasify "$PSScriptRoot\publish\Clowd.$($Ver).nupkg" `
# --setupIcon="$PSScriptRoot\src\Clowd\Images\default.ico" `
# --no-msi `
# --releaseDir="$PSScriptRoot\releases"
$MSBuildPath = (&"${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe) | Out-String
$MSBuildPath = $MSBuildPath.Trim();

Remove-Item "$PSScriptRoot\publish" -Recurse

&$MSBuildPath "$PSScriptRoot\src\Clowd\Clowd.csproj" `
/t:Restore,Rebuild,Publish `
/v:minimal `
/p:PublishSingleFile=False `
/p:SelfContained=True `
/p:PublishProtocol=FileSystem `
/p:Configuration=Release `
/p:Platform=x64 `
/p:PublishDir=$PSScriptRoot\publish\Clowd `
/p:RuntimeIdentifier=win-x64 `
/p:PublishReadyToRun=False `
/p:PublishTrimmed=False `
/p:SolutionDir=$PSScriptRoot 
# /p:AllowedReferenceRelatedFileExtensions=none

# &$MSBuildPath "$PSScriptRoot\src\Clowd.Installer\Clowd.Installer.csproj" `
# /t:Restore,Rebuild,Publish `
# /v:minimal `
# /p:PublishSingleFile=True `
# /p:SelfContained=True `
# /p:PublishProtocol=FileSystem `
# /p:Configuration=Release `
# /p:Platform=AnyCPU `
# /p:PublishDir=$PSScriptRoot\publish\ClowdCLI `
# /p:RuntimeIdentifier=win-x64 `
# /p:PublishReadyToRun=False `
# /p:PublishTrimmed=True `
# /p:SolutionDir=$PSScriptRoot `
# /p:AllowedReferenceRelatedFileExtensions=none `
# /p:IncludeNativeLibrariesForSelfExtract=True
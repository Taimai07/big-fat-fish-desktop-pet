$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$testExe = Join-Path $PSScriptRoot 'Tests.exe'
$frameworkDir = Split-Path -Parent $compiler
& $compiler /nologo /target:exe /platform:anycpu /utf8output /warnaserror /main:Tests "/win32manifest:$appRoot\src\app.manifest" "/out:$testExe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll "/r:$frameworkDir\WPF\UIAutomationClient.dll" "/r:$frameworkDir\WPF\UIAutomationTypes.dll" "/r:$frameworkDir\WPF\WindowsBase.dll" "$appRoot\src\Core.cs" "$appRoot\src\DesktopLayout.cs" "$appRoot\src\WindowsIconLabels.cs" "/resource:$appRoot\assets\background.png,DesktopInteraction.Assets.background.png" "/resource:$appRoot\assets\icon.png,DesktopInteraction.Assets.icon.png" "/win32icon:$appRoot\assets\app.ico" "/resource:$appRoot\assets\app.ico,DesktopInteraction.Assets.app.ico" "$appRoot\src\Runtime.cs" "$PSScriptRoot\Tests.cs" "$PSScriptRoot\DetectionCases.cs" "$PSScriptRoot\GeometryCases.cs" "$PSScriptRoot\FontCases.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler not found.' }
$appRoot = $PSScriptRoot
$frameworkDir = Split-Path -Parent $compiler
New-Item -ItemType Directory -Path "$appRoot\dist" -Force | Out-Null
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /warnaserror /main:DesktopInteraction.Program "/win32manifest:$appRoot\src\app.manifest" "/out:$appRoot\dist\大肥鱼桌宠.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll "/r:$frameworkDir\WPF\UIAutomationClient.dll" "/r:$frameworkDir\WPF\UIAutomationTypes.dll" "/r:$frameworkDir\WPF\WindowsBase.dll" "$appRoot\src\Core.cs" "$appRoot\src\DesktopLayout.cs" "$appRoot\src\WindowsIconLabels.cs" "/resource:$appRoot\assets\background.png,DesktopInteraction.Assets.background.png" "/resource:$appRoot\assets\icon.png,DesktopInteraction.Assets.icon.png" "/win32icon:$appRoot\assets\app.ico" "/resource:$appRoot\assets\app.ico,DesktopInteraction.Assets.app.ico" "$appRoot\src\Runtime.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output 'Built dist/Big Fat Fish Desktop Pet (.NET Framework, standard user privileges).'

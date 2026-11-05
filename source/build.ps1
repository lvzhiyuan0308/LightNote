$ErrorActionPreference = 'Stop'
$sourceDir = $PSScriptRoot
$releaseDir = Split-Path -Parent $sourceDir
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$wpfDir = Join-Path $frameworkDir 'WPF'
$compiler = Join-Path $frameworkDir 'csc.exe'
$refs = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $frameworkDir $_) }
$refs += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $wpfDir $_) }
$compilerArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', ('/out:' + (Join-Path $releaseDir 'LightNote.exe')), ('/win32manifest:' + (Join-Path $sourceDir 'app.manifest')), ('/win32icon:' + (Join-Path $sourceDir 'app.ico')), ('/resource:' + (Join-Path $sourceDir 'app.ico') + ',LightNote.ico'), ('/resource:' + (Join-Path $sourceDir 'app.png') + ',LightNote.png'), ('/resource:' + (Join-Path $sourceDir 'MainWindow.xaml') + ',MainWindow.xaml')) + $refs + @((Join-Path $sourceDir 'Models.cs'), (Join-Path $sourceDir 'App.cs'), (Join-Path $sourceDir 'SelfTest.cs'))
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $sourceDir 'LightNote.exe.config') -Destination $releaseDir -Force
Write-Output ('Built: ' + (Join-Path $releaseDir 'LightNote.exe'))

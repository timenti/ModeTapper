$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build-ModeTapper.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$source = 'outputs/task-screen-awake'
$files = @(
    'LICENSE',
    'work/ModeTapper.exe',
    'work/Install-ModeTapper.ps1',
    'work/ModeTapper-camera-control.exe',
    'work/Install-ModeTapperCamera.ps1',
    "$source/ModeTapper.exe.config",
    "$source/ModeTapper.ico",
    "$source/rules.default.pac"
)
$payload = Join-Path ([IO.Path]::GetTempPath()) ('ModeTapper-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    $archive = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $name), $name) | Out-Null
        }
    } finally { $archive.Dispose() }
    $folder = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $target = Join-Path $folder 'ModeTapper.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /optimize+ /target:winexe /platform:x64 '/reference:System.Windows.Forms.dll' '/reference:System.Drawing.dll' '/reference:System.IO.Compression.dll' '/reference:System.IO.Compression.FileSystem.dll' ("/win32manifest:"+(Join-Path $root "$source/ModeTapper.manifest")) ("/win32icon:"+(Join-Path $root "$source/ModeTapper.ico")) ("/resource:$payload,ModeTapper.Payload") ("/out:"+$target) (Join-Path $PSScriptRoot 'ModeTapperSetup.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика завершилась ошибкой' }
    $test = Start-Process -FilePath $target -ArgumentList '--verify-package' -WindowStyle Hidden -PassThru -Wait
    if ($test.ExitCode -ne 0) { throw 'Проверка содержимого установщика завершилась ошибкой' }
    Get-FileHash -LiteralPath $target
} finally {
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload }
}

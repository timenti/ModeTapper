param(
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCertificate,
    [string]$TimestampServer,
    [switch]$UseBuiltPayload,
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
if ($SigningCertificate -and !$TimestampServer) { throw 'Укажите сервер меток времени для подписи релиза.' }
function Sign-ReleaseFile([string]$path) {
    if (!$SigningCertificate) { return }
    $signature = Set-AuthenticodeSignature -LiteralPath $path -Certificate $SigningCertificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if ($signature.Status -ne 'Valid') { throw "Не удалось проверить подпись $path ($($signature.Status))" }
}
$root = Split-Path -Parent $PSScriptRoot
if (!$UseBuiltPayload) { & (Join-Path $PSScriptRoot 'Build-ModeTapper.ps1') }
Sign-ReleaseFile (Join-Path $PSScriptRoot 'ModeTapper.exe')
Sign-ReleaseFile (Join-Path $PSScriptRoot 'ModeTapper-camera-control.exe')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$source = 'outputs/task-screen-awake'
$files = [ordered]@{
    'LICENSE.txt' = 'LICENSE'
    'ModeTapper.exe' = 'work/ModeTapper.exe'
    'ModeTapper-camera-control.exe' = 'work/ModeTapper-camera-control.exe'
    'ModeTapper.exe.config' = "$source/ModeTapper.exe.config"
    'ModeTapper.ico' = "$source/ModeTapper.ico"
    'rules.default.pac' = "$source/rules.default.pac"
}
$payload = Join-Path ([IO.Path]::GetTempPath()) ('ModeTapper-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    $archive = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $files.Keys) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $files[$name]), $name) | Out-Null
        }
    } finally { $archive.Dispose() }
    $folder = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $target = Join-Path $folder 'ModeTapper.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /optimize+ /target:winexe /platform:x64 '/reference:System.Windows.Forms.dll' '/reference:System.Drawing.dll' '/reference:Accessibility.dll' '/reference:Microsoft.CSharp.dll' '/reference:System.IO.Compression.dll' '/reference:System.IO.Compression.FileSystem.dll' ("/win32manifest:"+(Join-Path $root "$source/ModeTapper.manifest")) ("/win32icon:"+(Join-Path $root "$source/ModeTapper.ico")) ("/resource:$payload,ModeTapper.Payload") ("/out:"+$target) (Join-Path $PSScriptRoot 'ModeTapperSetup.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика завершилась ошибкой' }
    if (!$BuildOnly) {
        $test = Start-Process -FilePath $target -ArgumentList '--verify-package' -WindowStyle Hidden -PassThru -Wait
        if ($test.ExitCode -ne 0) { throw 'Проверка содержимого установщика завершилась ошибкой' }
    }
    Sign-ReleaseFile $target
    $checksum = Get-FileHash -LiteralPath $target -Algorithm SHA256
    Set-Content -LiteralPath (Join-Path $folder 'SHA256SUMS.txt') -Value ($checksum.Hash.ToLowerInvariant() + '  ModeTapper.exe') -Encoding ascii
    $checksum
} finally {
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload }
}

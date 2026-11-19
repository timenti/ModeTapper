$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$sourceFolder=Join-Path $taskRoot 'outputs\task-screen-awake'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@('ModeTapper.cs','ModeTapperFlyout.cs','ModeTapperVpn.cs','ModeTapperVpnSites.cs','ModeTapperTouchpad.cs','ModeTapperTouchpadHardware.cs','ModeTapperTouchpadController.cs','ModeTapperTouchpadZoom.cs','ModeTapperHomeIndicator.cs','ModeTapperCarousel.cs','ModeTapperCarouselAssets.cs','ModeTapperTabletEdges.cs','ModeTapperFrameClock.cs','ModeTapperSafety.cs','ModeTapperPowerPolicy.cs','ModeTapperAsusCooling.cs','ModeTapperSideButton.cs','ModeTapperDesktopMotion.cs','ModeTapperDesktopEffects.cs','ModeTapperCodexWidget.cs','ModeTapperDesktopWidgets.cs','ModeTapperDesktopIcons.cs','ModeTapperDeviceControls.cs') | ForEach-Object {Join-Path $sourceFolder $_}
$automationLibraries=@('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll') | ForEach-Object {
    '/reference:'+(Join-Path (Split-Path -Parent $compiler) "WPF\$_")
}
$target=Join-Path $PSScriptRoot 'ModeTapper.exe'
& $compiler /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll /reference:Microsoft.CSharp.dll @automationLibraries ("/win32manifest:"+(Join-Path $sourceFolder 'ModeTapper.manifest')) ("/win32icon:"+(Join-Path $sourceFolder 'ModeTapper.ico')) ("/out:"+$target) @sources
if($LASTEXITCODE -ne 0){throw 'ModeTapper build failed'}
Get-FileHash -LiteralPath $target
$cameraWorker=Join-Path $PSScriptRoot 'ModeTapper-camera-control.exe'
& $compiler /nologo /target:winexe /optimize+ /define:LUMA_CAMERA_WORKER /reference:System.Drawing.dll /reference:System.Web.Extensions.dll ("/out:"+$cameraWorker) (Join-Path $sourceFolder 'ModeTapperDeviceControls.cs')
if($LASTEXITCODE -ne 0){throw 'ModeTapper camera worker build failed'}

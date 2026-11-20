param([string]$PreviousProfile,[switch]$CarouselTest,[switch]$SafeMode,[switch]$InstallCameraControls)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$build=Join-Path $PSScriptRoot 'ModeTapper.exe'
$install=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ModeTapper'
$savedInstall=Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\ModeTapper' -Name InstallLocation -ErrorAction SilentlyContinue
if($savedInstall -and (Test-Path -LiteralPath (Join-Path $savedInstall 'modetapper-managed-install.txt'))){$install=$savedInstall}
$legacy=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Luma'
if(!$PreviousProfile -and (Test-Path -LiteralPath (Join-Path $legacy 'luma-managed-install.txt'))){$PreviousProfile=$legacy}
if(!(Test-Path -LiteralPath $build)){throw 'Build ModeTapper first'}
if((Test-Path -LiteralPath $install) -and ((Get-Item -LiteralPath $install).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Install directory must not be a reparse point'}
if((Test-Path -LiteralPath "$install\ModeTapper.exe") -and !(Test-Path -LiteralPath "$install\modetapper-managed-install.txt")){throw 'Existing unrecognized ModeTapper installation: preserve it for inspection'}
if($CarouselTest -and $SafeMode){throw 'Choose either a dedicated carousel test or persistent safe mode'}
if($InstallCameraControls){
 $cameraWorker=Join-Path $PSScriptRoot 'ModeTapper-camera-control.exe'
 if(!(Test-Path -LiteralPath $cameraWorker -PathType Leaf)){throw 'Build the ModeTapper camera component first'}
}
$old=if($PreviousProfile){Join-Path $PreviousProfile 'Luma.exe'}else{$null}
$stopExe=if(Test-Path -LiteralPath "$install\ModeTapper.exe"){"$install\ModeTapper.exe"}else{$old}
$argument=if($CarouselTest){'--carousel-test'}elseif($SafeMode){'--safe-mode'}else{'normal'}
$statusFile=Join-Path $install 'tray-status.json'
$priorStatus=if($stopExe){Join-Path (Split-Path -Parent $stopExe) 'tray-status.json'}else{$statusFile}
$lease=$null
if($argument -eq 'normal' -and $stopExe -and (Test-Path -LiteralPath $priorStatus)){
 try{
  $status=Get-Content -LiteralPath $priorStatus -Raw | ConvertFrom-Json
  $oldModeTapper=Get-Process -Id $status.pid -ErrorAction SilentlyContinue
  if($status.sideButtonStoreBlocked -and $oldModeTapper -and $oldModeTapper.Path -eq $stopExe){
   # Bridge the verified user-session guard until the new process owns its handle.
   [Threading.EventWaitHandle]::TryOpenExisting('ATK Package AsMonStartupTask Application Event',[ref]$lease)|Out-Null
  }
 }catch{}
}
try{
 if($stopExe -and (Test-Path -LiteralPath $stopExe)){
  Start-Process -FilePath $stopExe -ArgumentList '--stop' -WindowStyle Hidden -Wait
 }
 $appPaths=@("$install\ModeTapper.exe")
 if($old){$appPaths+=$old}
 foreach($proc in @(Get-Process -Name ModeTapper,Luma -ErrorAction SilentlyContinue | Where-Object {$_.Path -in $appPaths})){
  if(!$proc.WaitForExit(10000)){throw 'ModeTapper is still running; no files overwritten'}
 }
 New-Item -ItemType Directory -Path $install -Force|Out-Null
 if(Test-Path -LiteralPath "$install\ModeTapper.exe"){
  $backup=Join-Path $install ('backups\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
  New-Item -ItemType Directory -Path $backup -Force|Out-Null
  Copy-Item -LiteralPath "$install\ModeTapper.exe" -Destination $backup
 }
 # Initial migration only: preserve all existing preferences and safety history.
 if($PreviousProfile){
  foreach($name in @('mode.txt','power-preset.txt','touchpad-enabled.txt','home-indicator-enabled.txt','screen-edges-enabled.txt','keyboard-level.txt','touchpad-zoom-enabled.txt','animation-plus-enabled.txt','codex-widget.json','desktop-widgets.json','camera-disabled.json')){
   $from=Join-Path $PreviousProfile $name;$to=Join-Path $install $name
   if((Test-Path -LiteralPath $from) -and !(Test-Path -LiteralPath $to)){Copy-Item -LiteralPath $from -Destination $to}
  }
  foreach($name in @('vpn','safety')){
   $from=Join-Path $PreviousProfile $name;$to=Join-Path $install $name
   if((Test-Path -LiteralPath $from) -and !(Test-Path -LiteralPath $to)){Copy-Item -LiteralPath $from -Destination $to -Recurse}
  }
 }
 $vpnFolder=Join-Path $install 'vpn'
 if(!(Test-Path -LiteralPath $vpnFolder)){
  New-Item -Path $vpnFolder -ItemType Directory|Out-Null
  Copy-Item -LiteralPath (Join-Path $project 'outputs/task-screen-awake/rules.default.pac') -Destination (Join-Path $vpnFolder 'rules.template.pac')
  [IO.File]::WriteAllText((Join-Path $vpnFolder 'custom-rules.json'),'{"direct":[],"proxy":[]}')
  [IO.File]::WriteAllText((Join-Path $vpnFolder 'disabled.flag'),'')
 }
 Copy-Item -LiteralPath $build -Destination "$install\ModeTapper.exe" -Force
 if($InstallCameraControls){Copy-Item -LiteralPath $cameraWorker -Destination $install -Force}
 foreach($name in @('ModeTapper.ico','ModeTapper.exe.config')){Copy-Item -LiteralPath (Join-Path $project "outputs\task-screen-awake\$name") -Destination (Join-Path $install $name) -Force}
 if(Test-Path -LiteralPath (Join-Path $project 'LICENSE')){
  Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $install 'LICENSE.txt') -Force
 }
 [IO.File]::WriteAllText("$install\modetapper-managed-install.txt",'ModeTapper managed installation; sources and tests are stored separately.')
 $shell=New-Object -ComObject WScript.Shell
 $programs=[Environment]::GetFolderPath('Programs')
 $task=Get-ScheduledTask -TaskName ModeTapper -ErrorAction SilentlyContinue
 if(!$task -and $old){$task=Get-ScheduledTask -TaskName Luma -ErrorAction SilentlyContinue}
 if($task){
  Export-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'luma-task-before-migration.xml') -Encoding UTF8
  $action=New-ScheduledTaskAction -Execute "$install\ModeTapper.exe" -Argument '--background' -WorkingDirectory $install
  Set-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Action $action|Out-Null
  $configured=(Get-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath).Actions
  $startup=$configured.Execute
  if($startup -ne "$install\ModeTapper.exe" -or $configured.Arguments -ne '--background'){throw 'Startup task readback mismatch'}
 }else{
  $startupFolder=[Environment]::GetFolderPath('Startup')
  if(!$startupFolder){throw 'Windows user startup folder unavailable'}
  $startup=Join-Path $startupFolder 'ModeTapper.lnk'
  $startupShortcut=$shell.CreateShortcut($startup)
  $startupShortcut.TargetPath="$install\ModeTapper.exe"
  $startupShortcut.Arguments='--background'
  $startupShortcut.WorkingDirectory=$install
  $startupShortcut.Save()
 }
 $shortcut=$shell.CreateShortcut((Join-Path $programs 'ModeTapper.lnk'));$shortcut.TargetPath="$install\ModeTapper.exe";$shortcut.Arguments='--side-panel';$shortcut.WorkingDirectory=$install;$shortcut.IconLocation="$install\ModeTapper.ico";$shortcut.Description='ModeTapper';$shortcut.Save()
 if($old){
  foreach($dir in $programs,[Environment]::GetFolderPath('Startup')){
   $legacyShortcut=Join-Path $dir 'Luma.lnk'
   if((Test-Path -LiteralPath $legacyShortcut) -and $shell.CreateShortcut($legacyShortcut).TargetPath -eq $old){
    Move-Item -LiteralPath $legacyShortcut -Destination (Join-Path $install ([IO.Path]::GetFileName($dir)+'-Luma.lnk.bak')) -Force
   }
  }
 }
 $launchArgument=if($argument -eq 'normal'){'--background'}else{$argument}
 $running=Start-Process -FilePath "$install\ModeTapper.exe" -ArgumentList $launchArgument -WindowStyle Hidden -PassThru
 $adopted=$false
 if($lease){
  $deadline=[Diagnostics.Stopwatch]::StartNew()
  do{
   try{
    $status=Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
    $adopted=$status.pid -eq $running.Id -and $status.sideButtonStoreBlocked
   }catch{}
   if($adopted -or $running.HasExited){break}
   Start-Sleep -Milliseconds 120
  }while($deadline.ElapsedMilliseconds -lt 8000)
  if(!$adopted){Write-Warning 'The new ModeTapper process did not confirm the ASUS launch guard before the upgrade lease expired.'}
 }
 $hash=[Security.Cryptography.SHA256]::Create()
 $binary=[IO.File]::OpenRead("$install\ModeTapper.exe")
 try{$checksum=[BitConverter]::ToString($hash.ComputeHash($binary)).Replace('-','')}
 finally{$binary.Dispose();$hash.Dispose()}
 [pscustomobject]@{installation=$install;mode=$argument;sha256=$checksum;startup=$startup;guardHandoff=$adopted}|ConvertTo-Json
}finally{
 if($lease){$lease.Dispose()}
}

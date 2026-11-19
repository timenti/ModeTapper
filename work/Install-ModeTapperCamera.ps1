param([Parameter(Mandatory)][string]$UserSid)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if($identity.User.Value -ne $UserSid -or !$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
 throw 'Use the same Windows administrator account to authorize the camera controls.'
}

$worker=Join-Path $PSScriptRoot 'ModeTapper-camera-control.exe'
if(!(Test-Path -LiteralPath $worker -PathType Leaf)){throw 'Build the Luma camera worker first.'}
$directory=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Luma Camera'
$directorySecurity=[Security.AccessControl.DirectorySecurity]::new()
$directorySecurity.SetSecurityDescriptorSddlForm('O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)')
if(Test-Path -LiteralPath $directory){
 $existing=Get-Item -LiteralPath $directory -Force
 if(!$existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)){
  throw 'The camera worker directory is not a protected directory.'
 }
 $owner=(Get-Acl -LiteralPath $directory).GetOwner([Security.Principal.SecurityIdentifier]).Value
 if($owner -notin 'S-1-5-32-544','S-1-5-18'){throw 'The owner of the camera worker directory is unexpected.'}
 Set-Acl -LiteralPath $directory -AclObject $directorySecurity
}else{
 [void][IO.Directory]::CreateDirectory($directory,$directorySecurity)
}
foreach($item in Get-ChildItem -LiteralPath $directory -Force){
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected link in the camera worker directory.'}
}

$target=Join-Path $directory 'LumaCameraControl.exe'
if(!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $worker).Hash){
 $staged=Join-Path $directory ('camera-worker-'+[Guid]::NewGuid().ToString('N')+'.exe')
 try{
  [IO.File]::Copy($worker,$staged)
  if(Test-Path -LiteralPath $target){[IO.File]::Replace($staged,$target,$null)}
  else{[IO.File]::Move($staged,$target)}
 }finally{if(Test-Path -LiteralPath $staged){[IO.File]::Delete($staged)}}
}

$journal=Join-Path $directory 'camera-disabled.json'
$legacy=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Luma\camera-disabled.json'
$alreadyUsed=(Test-Path -LiteralPath (Join-Path $directory 'block-result.txt')) -or
             (Test-Path -LiteralPath (Join-Path $directory 'enable-result.txt'))
if(!$alreadyUsed -and !(Test-Path -LiteralPath $journal) -and (Test-Path -LiteralPath $legacy -PathType Leaf) -and
    (Get-Item -LiteralPath $legacy).Length -le 16384){
 $saved=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($legacy))
 $disabled=@(Get-PnpDevice -PresentOnly -Class Camera | Where-Object Problem -eq CM_PROB_DISABLED)
 $migrated=@($disabled | Where-Object {$_.InstanceId -in $saved} | ForEach-Object InstanceId)
 if($migrated.Count){[IO.File]::WriteAllText($journal,(ConvertTo-Json -InputObject $migrated -Compress))}
}

$scheduler=New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$root=$scheduler.GetFolder('\')
# A filtered user token can read and invoke these fixed actions. Only administrators and SYSTEM can change them.
$taskSecurity='O:BAG:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;'+$UserSid+')'
foreach($operation in 'block','enable'){
 $name='Luma-Camera-'+$operation
 $task=$scheduler.NewTask(0)
 $task.RegistrationInfo.Description="Luma camera control: $operation Camera-class devices on demand."
 $task.Principal.UserId=$UserSid
 $task.Principal.LogonType=3
 $task.Principal.RunLevel=1
 $task.Settings.DisallowStartIfOnBatteries=$false
 $task.Settings.StopIfGoingOnBatteries=$false
 $task.Settings.ExecutionTimeLimit='PT1M'
 $task.Settings.MultipleInstances=2
 $action=$task.Actions.Create(0)
 $action.Path=$target
 $action.Arguments=$operation
 $action.WorkingDirectory=$directory
 $registered=$root.RegisterTaskDefinition($name,$task,0x16,$UserSid,$null,3,$taskSecurity)
 if($registered.Definition.Actions.Item(1).Path -ne $target -or
    $registered.Definition.Actions.Item(1).Arguments -ne $operation -or
    $registered.Definition.Principal.RunLevel -ne 1 -or $registered.Definition.Triggers.Count -ne 0){
  throw "Registered camera command does not match the expected definition: $name"
 }
}

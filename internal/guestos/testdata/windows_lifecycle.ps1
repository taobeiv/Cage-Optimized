param([Parameter(Mandatory)][string]$Query)
$ErrorActionPreference='Stop'
$script:targetId=[guid]'9e97dd9a-eb36-489a-bb1d-8d28a0d78682'
$script:state='Off';$script:nics=0;$script:mutations=0;$script:generation=2;$script:secure='On'
New-Module -Name Hyper-V -ScriptBlock {
 function Get-VM { param([Guid]$Id) if ($Id -ne $script:targetId) { throw 'Unscoped VM operation.' }; [pscustomobject]@{Id=$Id;State=$script:state;Generation=$script:generation} }
 function Get-VMFirmware { param($VM) if ($VM.Id -ne $script:targetId) { throw 'Wrong VM.' };[pscustomobject]@{SecureBoot=$script:secure;SecureBootTemplate='MicrosoftWindows'} }
 function Get-VMSecurity { param($VM) [pscustomobject]@{TpmEnabled=$true} }
 function Get-VMNetworkAdapter { param($VM) for($i=0;$i -lt $script:nics;$i++){[pscustomobject]@{Id=$i}} }
 function Start-VM { param($VM) if($VM.Id -ne $script:targetId){throw 'Wrong VM.'};$script:startAttempts++;if($script:startFails){throw 'Resume failed.'};$script:state='Running';$script:mutations++ }
 function Remove-VMSavedState { param($VM) $script:discarded++;$script:state='Off' }
 function Stop-VM { param($VM,[switch]$TurnOff,[switch]$Force) if($TurnOff -or $Force -or $VM.Id -ne $script:targetId){throw 'Unsafe stop.'};$script:state='Off';$script:mutations++ }
 Export-ModuleMember -Function *
} | Import-Module -Force
# Module state is independent: configure it deliberately for each case.
$module=Get-Module Hyper-V
& $module { $script:targetId=[guid]'9e97dd9a-eb36-489a-bb1d-8d28a0d78682';$script:state='Off';$script:nics=0;$script:mutations=0;$script:generation=2;$script:secure='On' }
$action='start';$result=(& $Query)|ConvertFrom-Json
if($result.state -ne 'Running'){throw 'Start failed.'}
$action='stop';$result=(& $Query)|ConvertFrom-Json
if($result.state -ne 'Off'){throw 'Stop failed.'}
$action='stop';$result=(& $Query)|ConvertFrom-Json
if((& $module {$script:mutations}) -ne 2){throw 'Non-idempotent stop.'}
foreach($unsafe in 'nic','generation','secure'){
 & $module { $script:nics=0;$script:generation=2;$script:secure='On' }
 switch($unsafe){'nic'{& $module {$script:nics=1}}'generation'{& $module {$script:generation=1}}'secure'{& $module {$script:secure='Off'}}}
 $action='start';$rejected=$false;try{& $Query | Out-Null}catch{$rejected=$true}
 if(!$rejected -or (& $module {$script:mutations}) -ne 2){throw "Unsafe $unsafe guest started."}
}
# A saved guest must resume directly, with no discarded state or cold-start retry.
& $module { $script:state='Saved';$script:nics=0;$script:generation=2;$script:secure='On';$script:mutations=0;$script:startAttempts=0;$script:discarded=0;$script:startFails=$false }
$action='start';$result=(& $Query)|ConvertFrom-Json
if($result.state -ne 'Running' -or (& $module {$script:startAttempts}) -ne 1 -or (& $module {$script:mutations}) -ne 1 -or (& $module {$script:discarded}) -ne 0){throw 'Saved guest was not resumed safely.'}
$result=(& $Query)|ConvertFrom-Json
if($result.state -ne 'Running' -or (& $module {$script:startAttempts}) -ne 1){throw 'Running guest was started again.'}

& $module { $script:state='Saved';$script:startFails=$true;$script:startAttempts=0;$script:mutations=0 }
$rejected=$false;try{& $Query | Out-Null}catch{$rejected=$true}
if(!$rejected -or (& $module {$script:state}) -ne 'Saved' -or (& $module {$script:startAttempts}) -ne 1 -or (& $module {$script:mutations}) -ne 0 -or (& $module {$script:discarded}) -ne 0){throw 'Failed resume did not preserve saved state.'}
& $module { $script:startFails=$false;$script:startAttempts=0 }

foreach($state in 'Paused','Starting','Saving','SavedCritical'){
 & $module { param($state) $script:state=$state } $state
 $rejected=$false;try{& $Query | Out-Null}catch{$rejected=$true}
 if(!$rejected -or (& $module {$script:startAttempts}) -ne 0 -or (& $module {$script:mutations}) -ne 0){throw "Unsupported $state guest started."}
}
foreach($unsafe in 'nic','generation','secure'){
 & $module { $script:state='Saved';$script:nics=0;$script:generation=2;$script:secure='On' }
 switch($unsafe){'nic'{& $module {$script:nics=1}}'generation'{& $module {$script:generation=1}}'secure'{& $module {$script:secure='Off'}}}
 $rejected=$false;try{& $Query | Out-Null}catch{$rejected=$true}
 if(!$rejected -or (& $module {$script:state}) -ne 'Saved' -or (& $module {$script:startAttempts}) -ne 0 -or (& $module {$script:discarded}) -ne 0){throw "Unsafe $unsafe saved guest resumed."}
}
$action='stop';$rejected=$false;try{& $Query | Out-Null}catch{$rejected=$true}
if(!$rejected -or (& $module {$script:state}) -ne 'Saved' -or (& $module {$script:mutations}) -ne 0 -or (& $module {$script:discarded}) -ne 0){throw 'Stopping a saved guest discarded its state.'}
'Windows lifecycle exact VM, saved state recovery, and safe shutdown checks passed.'

$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../windows/grant-console-access.ps1')
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$vm=[guid]'564eb004-cf4a-4143-9984-e26c4204ae19'
$binding=[pscustomobject]@{version=1;vm_id=$vm.ToString();host_sid=$sid;disk_path='C:\fixture\guest.vhdx';disk_volume=42;disk_file_id=71}
# Exercise the actual grant coordinator with a simulated Hyper-V boundary.
$fake=New-Module -Name Hyper-V -ScriptBlock {
    $script:grants=0; $script:present=$false
    function Get-VMConnectAccess { param($VMId,$UserName) if($script:present){[pscustomobject]@{VMId=$VMId;UserName=$UserName}} }
    function Grant-VMConnectAccess { param($VMId,$UserName) $script:grants++;$script:present=$true }
    function Get-GrantCount { $script:grants }
    Export-ModuleMember -Function Get-VMConnectAccess,Grant-VMConnectAccess,Get-GrantCount
}
Import-Module $fake -Force
try {
    foreach($field in @('host_sid','vm_id','disk_path','disk_volume','disk_file_id')){
        $changed=$binding|ConvertTo-Json|ConvertFrom-Json
        $changed.$field='changed'
        $rejected=$false
        try { Grant-CageConsoleAccess $vm $sid $binding $changed } catch { $rejected=$true }
        if(!$rejected -or (Get-GrantCount) -ne 0){throw "Changed $field was not rejected before granting"}
    }
    $wrongOwner='S-1-5-21-1-2-3-9999';$rejected=$false
    try { Grant-CageConsoleAccess $vm $wrongOwner $binding $binding } catch { $rejected=$true }
    if(!$rejected -or (Get-GrantCount) -ne 0){throw 'Wrong caller owner was accepted'}
    Grant-CageConsoleAccess $vm $sid $binding $binding
    Grant-CageConsoleAccess $vm $sid $binding $binding
    if((Get-GrantCount) -ne 1){throw 'Grant is not idempotent'}
    'Console access: 6 mismatches rejected before mutation; grant read back; repeat made no additional grant.'
} finally { Remove-Module $fake }

# Embedded in the native application. Defines only the grant coordinator;
# the elevated entry point supplies protected and freshly verified identities.
function Grant-CageConsoleAccess([guid]$VMId, [string]$OwnerSID, $Binding, $Live) {
    if ($VMId -eq [guid]::Empty -or $OwnerSID -notmatch '^S-1-(5-21|12-1)-[0-9-]+$' -or
        !$Binding -or !$Live -or $Binding.version -ne 1 -or $Binding.vm_id -cne $VMId.ToString('D') -or
        $Binding.host_sid -cne $OwnerSID) { throw 'Console binding does not match the requested owner and VM.' }
    foreach ($field in @('version','vm_id','host_sid','disk_path','disk_volume','disk_file_id')) {
        if (!$Binding.$field -or [string]$Binding.$field -cne [string]$Live.$field) {
            throw 'The protected VM or disk identity changed. Connection permissions were preserved.'
        }
    }
    $account=([Security.Principal.SecurityIdentifier]::new($OwnerSID)).Translate([Security.Principal.NTAccount]).Value
    $access=@(Hyper-V\Get-VMConnectAccess -VMId $VMId -UserName $account -ErrorAction Stop)
    if (!$access.Count) { Hyper-V\Grant-VMConnectAccess -VMId $VMId -UserName $account -ErrorAction Stop }
    if (!@(Hyper-V\Get-VMConnectAccess -VMId $VMId -UserName $account -ErrorAction Stop).Count) {
        throw 'Could not verify connection access.'
    }
}

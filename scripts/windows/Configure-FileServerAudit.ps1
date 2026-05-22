[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths,

    [string]$Identity = "Everyone",

    [string]$AuditFlags = "Success,Failure",

    [string]$InheritanceFlags = "ContainerInherit,ObjectInherit",

    [string]$PropagationFlags = "None"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

foreach ($path in $Paths) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "O caminho '$path' nao existe."
    }

    $acl = Get-Acl -LiteralPath $path
    $rights = [System.Security.AccessControl.FileSystemRights]"CreateFiles,CreateDirectories,WriteData,AppendData,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership,WriteAttributes,WriteExtendedAttributes"
    $inheritance = [System.Security.AccessControl.InheritanceFlags]$InheritanceFlags
    $propagation = [System.Security.AccessControl.PropagationFlags]$PropagationFlags
    $flags = [System.Security.AccessControl.AuditFlags]$AuditFlags
    $rule = New-Object System.Security.AccessControl.FileSystemAuditRule($Identity, $rights, $inheritance, $propagation, $flags)

    if ($PSCmdlet.ShouldProcess($path, "Configurar auditoria NTFS para $Identity")) {
        $acl.AddAuditRule($rule)
        Set-Acl -LiteralPath $path -AclObject $acl
    }
}

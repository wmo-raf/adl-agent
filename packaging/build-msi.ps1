<#
.SYNOPSIS
    Build the service tier's MSI from the sources in msi/.

.DESCRIPTION
    Its own script because two callers need the same package built the same
    way. pack.ps1 builds the one a country installs; verify-msi-install.ps1
    builds the one it then upgrades that install with, at the next version,
    because a major upgrade is the operation this package has to survive and
    there is no way to perform one with a single package.

    Written out twice, those two would drift -- a source file added to one, an
    extension added to the other -- and the second package is the only thing
    that ever proves an unattended upgrade keeps a machine's address. So it is
    written once.

    It does not install the WiX toolset; pack.ps1 does that, and this expects
    to be run after it.

.PARAMETER Version
    Three numbers, as the agent reports itself. Windows Installer compares
    only three fields of a product version.

.PARAMETER Output
    Where to write the .msi.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $Output,

    [Parameter(Mandatory = $true)]
    [string] $ServiceDirectory,

    [Parameter(Mandatory = $true)]
    [string] $TrayDirectory,

    [Parameter(Mandatory = $true)]
    [string] $AssetsDirectory
)

$ErrorActionPreference = "Stop"

# Util for the service's failure actions and the state folder's permissions;
# UI for the dialog set the one screen this installer shows is built from.
wix build `
    (Join-Path $PSScriptRoot "msi/AdlAgent.wxs") `
    (Join-Path $PSScriptRoot "msi/AdlAgentUI.wxs") `
    -arch x64 `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -d Version=$Version `
    -d ServiceDir=$ServiceDirectory `
    -d TrayDir=$TrayDirectory `
    -d AssetsDir=$AssetsDirectory `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    throw "Building $Output failed with exit code $LASTEXITCODE."
}

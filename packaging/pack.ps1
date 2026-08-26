<#
.SYNOPSIS
    Build everything a country server can be installed from.

.DESCRIPTION
    One script rather than a list of steps in a CI file, so that the packages
    an NMHS installs can be reproduced by a person on a Windows machine
    exactly as the build produced them — which is the only way to work out
    what went wrong with an install nobody can reach.

    It produces, into -OutputDirectory:

      AdlAgent-<version>-x64.msi        the service tier (administrator)
      AdlAgent-<version>-full.nupkg     the per-user tier's upgrade package
      AdlAgent-<version>-Setup.exe      the per-user tier's installer

    Windows only. The MSI is built by the WiX toolset and the per-user
    packages by Velopack's `vpk`, both installed here as .NET tools if they
    are not already present.

.PARAMETER Version
    Three numbers, as the agent reports itself: 1.2.0. Windows Installer
    compares only three fields of a product version, which is why the update
    feed refuses anything else.

.PARAMETER WithPerUserTier
    Build the per-user tier as well. Nothing does, by default and on purpose:
    no site has asked for it, it has never been run on a Windows machine, and
    a tier nobody installs is one whose packaging is proven by nothing. The
    code behind it is untouched — see "the per-user tier" in the README.

.EXAMPLE
    ./packaging/pack.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $OutputDirectory = "artifacts",

    [string] $Runtime = "win-x64",

    # The per-user tier, which nothing asks for.
    #
    # It was the other way round for a week -- both tiers by default, with a
    # -MsiOnly for the dev loop -- and the default nothing ever took is the
    # one that rots. What ships is the MSI, so what this script does when it
    # is given no opinion is build the MSI: CI calls it bare, dev/pack.cmd
    # calls it bare, and a person reproducing a release calls it bare. That
    # is the whole premise of there being one script.
    [switch] $WithPerUserTier
)

$ErrorActionPreference = "Stop"

$repository = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repository "publish"
# Relative to the repository, as CI passes it, or absolute, as a caller
# writing somewhere else does -- dev/pack.cmd builds out of a mirror of the
# source and puts the packages next to it rather than inside it. Join-Path
# does not notice a rooted second argument and would compose the two into a
# path with a colon in the middle of it, which Windows rejects only once
# something tries to create it.
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $repository $OutputDirectory
}

# The icons. Committed rather than generated here: `dotnet build` has no
# dependencies on either CI leg or on the Windows machine somebody reproduces
# a release from, and a rasteriser in this script would be a dependency on all
# of them. They are rebuilt by hand with assets/render-icons.sh when the brand
# changes -- see assets/README.md.
$assets = Join-Path $repository "assets"
$productIcon = Join-Path $assets "adl-agent-tray.ico"

# What Velopack shows while it installs. The same picture the MSI puts down
# the left of its first and last screens, as a PNG rather than a BMP: this is
# not a Windows Installer bitmap and none of that format's rules apply to it.
# The two tiers are one product, and a technician who has seen one installer
# should recognise the other.
$splashImage = Join-Path $assets "installer-splash.png"

# The tool versions are pinned. A packaging tool that moved under a release
# would change what a fleet installs without anything in this repository
# having changed.
$wixVersion = "6.0.2"
$velopackVersion = "1.2.0"

function Invoke-Step {
    param([string] $Name, [scriptblock] $Body)

    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body

    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Install-DotnetTool {
    param([string] $Package, [string] $ToolVersion, [string] $Command)

    if (Get-Command $Command -ErrorAction SilentlyContinue) {
        return
    }

    Invoke-Step "Installing $Package $ToolVersion" {
        dotnet tool install --global $Package --version $ToolVersion
    }

    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
Remove-Item -Recurse -Force -Path $publish -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# The two programs, each carrying the .NET runtime inside it
#
# Separate folders on purpose: the tray's publish brings a few of the service
# project's own artefacts with it (it references that project), and an
# installer should be given two clean directories rather than one it has to
# pick apart.
# ---------------------------------------------------------------------------

$serviceDir = Join-Path $publish "service"
$trayDir = Join-Path $publish "tray"

Invoke-Step "Publishing the service" {
    dotnet publish (Join-Path $repository "src/AdlAgent.Windows/AdlAgent.Windows.csproj") `
        -c Release -r $Runtime -p:Version=$Version -o $serviceDir
}

Invoke-Step "Publishing the tray" {
    dotnet publish (Join-Path $repository "src/AdlAgent.Tray/AdlAgent.Tray.csproj") `
        -c Release -r $Runtime -p:Version=$Version -o $trayDir
}

# ---------------------------------------------------------------------------
# The service tier: an MSI that installs a Windows Service
# ---------------------------------------------------------------------------

Install-DotnetTool -Package "wix" -ToolVersion $wixVersion -Command "wix"

# Util for the service's failure actions and the state folder's permissions;
# UI for the dialog set the one screen this installer shows is built from.
# Both pinned to $wixVersion, and pinned to the same one: a dialog library and
# the compiler that links it are one toolset, and mixing versions is a class of
# breakage nobody would look for in a packaging script.
$wixExtensions = @(
    "WixToolset.Util.wixext",
    "WixToolset.UI.wixext"
)

foreach ($extension in $wixExtensions) {
    Invoke-Step "Adding $extension $wixVersion" {
        wix extension add --global "$extension/$wixVersion"
    }
}

$msi = Join-Path $output "AdlAgent-$Version-x64.msi"

# In build-msi.ps1 rather than here, because verify-msi-install.ps1 has to
# build the same package at the next version to upgrade an install with, and
# two copies of this would drift.
Invoke-Step "Building $msi" {
    & (Join-Path $PSScriptRoot "build-msi.ps1") `
        -Version $Version `
        -Output $msi `
        -ServiceDirectory $serviceDir `
        -TrayDirectory $trayDir `
        -AssetsDirectory $assets
}

# ---------------------------------------------------------------------------
# The per-user tier: a Velopack install for a technician without
# administrator rights (story 3)
#
# Both programs go in one package. The main executable is the agent itself
# rather than the tray, because what this tier has to keep doing is
# collecting and sending: the shortcut Velopack puts in Startup is the whole
# of what "runs at logon" means here, and the tray is started from the Start
# menu when somebody wants to look at it.
#
# --icon is the tray's teal mark even though --mainExe is the service, because
# what it brands is Setup.exe and the shortcuts a technician clicks. Slate is
# reserved for the service's own executable, where its only job is to be
# distinguishable from the tray at sixteen pixels in Task Manager.
# ---------------------------------------------------------------------------

if (-not $WithPerUserTier) {
    Write-Host "==> Not building the per-user tier (-WithPerUserTier builds it)" -ForegroundColor DarkGray
}
else {
    Install-DotnetTool -Package "vpk" -ToolVersion $velopackVersion -Command "vpk"

    $userTierDir = Join-Path $publish "user"

    New-Item -ItemType Directory -Force -Path $userTierDir | Out-Null
    Copy-Item (Join-Path $serviceDir "adl-agent.exe") $userTierDir
    Copy-Item (Join-Path $trayDir "adl-agent-tray.exe") $userTierDir

    Invoke-Step "Packing the per-user tier" {
        vpk pack `
            --packId AdlAgent `
            --packTitle "ADL Agent" `
            --packAuthors "WMO RAF" `
            --packVersion $Version `
            --packDir $userTierDir `
            --mainExe adl-agent.exe `
            --icon $productIcon `
            --splashImage $splashImage `
            --shortcuts StartMenu,Startup `
            --outputDir $output
    }
}

Write-Host ""
Write-Host "Packages in $output" -ForegroundColor Green

Get-ChildItem $output | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "{0,-40} {1,12:N0}  {2}" -f $_.Name, $_.Length, $hash
}

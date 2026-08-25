<#
.SYNOPSIS
    Install the packaged MSI on this machine and read what it wrote.

.DESCRIPTION
    InstallerDialogTests reads the installer's sources and checks the rule its
    screen enforces against the rule the agent enforces. That runs everywhere,
    on every commit, and it cannot answer the question this script exists for:
    what does Windows Installer actually do with the package that was just
    built.

    Four things, and the third one is the one that matters:

      * an unattended install passed ADLURL= writes the address it was given,
        which is the path every existing document and every existing site
        uses;
      * an unattended install passed nothing at all still installs, because
        that is what a self-update is -- `msiexec /i ... /qn`, no properties,
        nobody watching. A screen that had grown a launch condition, or a
        custom action, or a default, would fail here and only here;
      * a major upgrade passed nothing leaves the address alone. This is the
        one that would break every machine in every fleet at once, quietly, at
        whatever hour they update themselves;
      * and an install ends with something to open the window with, in all
        three of the places a technician might look for it -- and removing the
        product takes all three away again.

    The upgrade package is built here rather than downloaded: the same
    sources, at a higher version, which is exactly what a release is. Building
    it takes seconds because both programs have already been published by
    pack.ps1.

    Windows only, and it needs administrator rights -- it installs a service.

.PARAMETER Msi
    The package to verify. Defaults to the newest one pack.ps1 produced.

.PARAMETER PublishDirectory
    Where pack.ps1 put its publish output, for building the upgrade package.

.EXAMPLE
    ./packaging/verify-msi-install.ps1
#>
[CmdletBinding()]
param(
    [string] $Msi,

    [string] $PublishDirectory = "publish",

    # Deliberately unresolvable. Nothing here is a test of whether an ADL
    # answers -- the installer does not ask, on purpose -- and pointing a
    # freshly installed service at somebody's real instance would be a
    # surprising thing for a packaging job to do.
    [string] $Address = "https://adl.verify.invalid"
)

$ErrorActionPreference = "Stop"

$repository = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repository $PublishDirectory
$serviceDir = Join-Path $publish "service"
$trayDir = Join-Path $publish "tray"
$assets = Join-Path $repository "assets"
$settings = Join-Path $env:ProgramData "ADL Agent\agent.ini"

# The three places a technician might look for the window, resolved rather
# than spelled out: this package is perMachine, so Windows Installer puts all
# three in their all-users variants and a hard-coded profile path would be
# checking the wrong machine.
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "ADL Agent\ADL Agent.lnk"),
    (Join-Path ([Environment]::GetFolderPath("CommonStartup")) "ADL Agent.lnk"),
    (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "ADL Agent.lnk")
)

# pack.ps1 puts the WiX toolset on its own process's PATH and this is a
# different process, so a step that runs straight after it still has to look.
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "The WiX toolset is not on this machine. Run packaging/pack.ps1 first."
}

if (-not $Msi) {
    $Msi = Get-ChildItem (Join-Path $repository "artifacts") -Filter "AdlAgent-*-x64.msi" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Msi -or -not (Test-Path $Msi)) {
    throw "There is no package to verify. Run packaging/pack.ps1 first."
}

Write-Host "==> Verifying $Msi" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# What is in the package
#
# Read out of the built database rather than out of the sources, because the
# question is what a compiler and a linker made of them. A dialog that failed
# to link is not an error anybody sees: the package builds, installs, and
# shows a progress bar.
# ---------------------------------------------------------------------------

function Get-MsiRows {
    param([string] $Path, [string] $Query, [int] $Columns)

    $installer = New-Object -ComObject WindowsInstaller.Installer

    $database = $installer.GetType().InvokeMember(
        "OpenDatabase", "InvokeMethod", $null, $installer, @($Path, 0))

    try {
        $view = $database.GetType().InvokeMember(
            "OpenView", "InvokeMethod", $null, $database, @($Query))

        $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null

        $rows = @()

        while ($true) {
            $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)

            if (-not $record) {
                break
            }

            $values = @()

            for ($column = 1; $column -le $Columns; $column++) {
                $values += $record.GetType().InvokeMember(
                    "StringData", "GetProperty", $null, $record, $column)
            }

            $rows += , $values
        }

        $view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null) | Out-Null

        # The comma keeps PowerShell from unrolling the rows on the way out,
        # which would turn a single row into its own columns.
        return , $rows
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
    }
}

Write-Host "==> The screen is in the package" -ForegroundColor Cyan

if ((Get-MsiRows $Msi "SELECT Dialog FROM Dialog WHERE Dialog='AdlUrlDlg'" 1).Count -ne 1) {
    throw "The package has no AdlUrlDlg. Double-clicking it would install a machine that has no idea where to report."
}

$field = Get-MsiRows $Msi "SELECT Control, Property FROM Control WHERE Dialog_='AdlUrlDlg' AND Type='Edit'" 2

if ($field.Count -ne 1 -or $field[0][1] -ne "ADLURL") {
    throw "AdlUrlDlg has no edit field bound to ADLURL, so nothing anybody types would be installed."
}

$conditions = Get-MsiRows $Msi "SELECT Control_, Action FROM ControlCondition WHERE Dialog_='AdlUrlDlg'" 2
$next = @()

foreach ($condition in $conditions) {
    if ($condition[0] -eq "Next") {
        $next += $condition[1]
    }
}

if (($next -notcontains "Enable") -or ($next -notcontains "Disable")) {
    throw "AdlUrlDlg's Next button is not conditioned both ways, so it would not follow what is typed."
}

# And that somebody double-clicking it is taken there. The screen is reached
# from the welcome screen rather than scheduled, so both halves matter: a
# sequenced welcome, and something that navigates from it.
$welcome = Get-MsiRows $Msi "SELECT Action FROM InstallUISequence WHERE Action='WelcomeDlg'" 1

if ($welcome.Count -ne 1) {
    throw "Nothing is sequenced to show a dialog, so double-clicking the package would install silently and ask nothing."
}

$navigation = Get-MsiRows $Msi "SELECT Dialog_, Control_ FROM ControlEvent WHERE Argument='AdlUrlDlg'" 2

if ($navigation.Count -eq 0) {
    throw "AdlUrlDlg is in the package but nothing leads to it."
}

# And nothing new that a silent install would also run. `/qn` shows no dialog
# whatever a package asks for, so the way a screen breaks the unattended path
# is never the screen -- it is a custom action scheduled beside it, which runs
# on every unattended upgrade in the fleet.
#
# Not none: the WiX util extension brings its own for the service's failure
# actions and the state folder's permissions, and those are named for it. What
# this refuses is one of ours.
$customActions = @()

try {
    $customActions = Get-MsiRows $Msi "SELECT Action FROM CustomAction" 1
}
catch {
    # No CustomAction table at all, which is also an answer.
}

foreach ($action in $customActions) {
    if ($action[0] -notlike "Wix*") {
        throw "The package carries a custom action of its own, '$($action[0])'. It would run on every silent self-update in the fleet."
    }
}

Write-Host "    custom actions: $(($customActions | ForEach-Object { $_[0] }) -join ', ')"

# ---------------------------------------------------------------------------
# The install ends with the window open
#
# The Exit dialog presses one of the util extension's own actions rather than
# one of ours, which is why the check above still passes. What has to be true
# of it is the other half: that it is in the package and in no sequence. A
# scheduled one would run on every silent self-update in the fleet, on servers
# with nobody logged on.
# ---------------------------------------------------------------------------

Write-Host "==> The last screen opens the tray" -ForegroundColor Cyan

$launch = "Wix4ShellExec_X64"

if (($customActions | Where-Object { $_[0] -eq $launch }).Count -ne 1) {
    throw "The package has no $launch, so the finish button would press an action that is not there."
}

foreach ($sequence in @("InstallUISequence", "InstallExecuteSequence")) {
    $scheduled = Get-MsiRows $Msi "SELECT Action FROM $sequence WHERE Action='$launch'" 1

    if ($scheduled.Count -ne 0) {
        throw "$launch is scheduled in $sequence. It would open a window on every silent upgrade in the fleet."
    }
}

$pressed = Get-MsiRows $Msi `
    "SELECT Dialog_, Control_, Event, Condition FROM ControlEvent WHERE Dialog_='ExitDialog' AND Argument='$launch'" 4

if ($pressed.Count -ne 1 -or $pressed[0][1] -ne "Finish" -or $pressed[0][2] -ne "DoAction") {
    throw "Nothing on the Exit dialog presses $launch, so a finished install would still show nobody anything."
}

# And only on a fresh install. A repair and an uninstall come through this
# same dialog, and the second would launch a program the same transaction has
# just deleted -- which is a broken window in front of somebody who was
# removing the product.
if ($pressed[0][3] -notmatch '(?i)NOT\s+Installed') {
    throw "The Exit dialog presses $launch under '$($pressed[0][3])'. It has to be a fresh install: uninstalling would open a program that is no longer there."
}

$target = Get-MsiRows $Msi "SELECT Value FROM Property WHERE Property='WixShellExecTarget'" 1

if ($target.Count -ne 1 -or -not $target[0][0]) {
    throw "WixShellExecTarget is not in the package, so the finish button would open nothing."
}

Write-Host "    $($pressed[0][1]) does $launch on $($target[0][0])"

# ---------------------------------------------------------------------------
# And leaves the window somewhere to be found again
#
# Three shortcuts, and each answers a different person's question: the Start
# menu for somebody who knows this is installed, the Startup folder for
# whoever logs on to the server next, and the desktop for the technician who
# has just watched the install finish. All three are all-users, because the
# package is perMachine.
# ---------------------------------------------------------------------------

$shortcuts = Get-MsiRows $Msi "SELECT Shortcut, Directory_ FROM Shortcut" 2
$folders = @($shortcuts | ForEach-Object { $_[1] })

foreach ($folder in @("DesktopFolder", "StartupFolder", "AgentMenuFolder")) {
    if ($folders -notcontains $folder) {
        throw "The package installs no shortcut in $folder. It has: $($folders -join ', ')."
    }
}

Write-Host "    shortcuts in $($folders -join ', ')"

# No default for the address. This is what an unattended upgrade rests on: it
# is passed no properties, so ADLURL is empty, so the component that writes
# the setting is not installed, so what is on disk is left alone.
$default = Get-MsiRows $Msi "SELECT Value FROM Property WHERE Property='ADLURL'" 1

if ($default.Count -ne 0 -and $default[0][0]) {
    throw "ADLURL defaults to '$($default[0][0])' in the package. An upgrade passed no properties would rewrite the setting it was supposed to leave alone."
}

# ---------------------------------------------------------------------------
# Installing it
# ---------------------------------------------------------------------------

function Invoke-Msi {
    param([string] $Name, [string[]] $Arguments)

    $log = Join-Path ([System.IO.Path]::GetTempPath()) "adl-agent-$Name.log"

    Write-Host "==> $Name" -ForegroundColor Cyan

    $msiexec = Start-Process -FilePath "msiexec.exe" -Wait -PassThru `
        -ArgumentList ($Arguments + @("/qn", "/norestart", "/l*v", "`"$log`""))

    # 3010 is "installed, and this machine would like a reboot". A packaging
    # check is not the place to argue about that.
    if ($msiexec.ExitCode -ne 0 -and $msiexec.ExitCode -ne 3010) {
        Write-Host "==> The last 60 lines of $log" -ForegroundColor Yellow
        Get-Content $log -Tail 60

        throw "$Name failed with exit code $($msiexec.ExitCode)."
    }
}

function Assert-ServiceRunning {
    param([string] $Message)

    # Given a few seconds. The package starts the service inside the install
    # transaction and waits for it, so this should be true the moment msiexec
    # returns; the wait is for the Service Control Manager still reporting
    # StartPending on a slow machine, which would be a false alarm rather than
    # a finding.
    foreach ($attempt in 1..15) {
        $service = Get-Service "ADL Agent" -ErrorAction SilentlyContinue

        if ($service -and $service.Status -eq "Running") {
            return
        }

        Start-Sleep -Seconds 2
    }

    $service = Get-Service "ADL Agent" -ErrorAction SilentlyContinue

    throw "$Message It is $(if ($service) { $service.Status } else { 'not installed at all' })."
}

function Read-Address {
    if (-not (Test-Path $settings)) {
        throw "There is no $settings, so the installed machine does not know where to report."
    }

    $line = Get-Content $settings |
        Where-Object { $_ -match '^\s*AdlBaseUrl\s*=' } |
        Select-Object -First 1

    if (-not $line) {
        throw "$settings holds no AdlBaseUrl:`n$(Get-Content $settings -Raw)"
    }

    return ($line -split "=", 2)[1].Trim()
}

$version = (Get-MsiRows $Msi "SELECT Value FROM Property WHERE Property='ProductVersion'" 1)[0][0]
$parts = $version.Split(".")
$upgradeVersion = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
$upgradeMsi = Join-Path ([System.IO.Path]::GetTempPath()) "AdlAgent-$upgradeVersion-x64.msi"

$installed = $false
$verified = $false

try {
    # 1. The command line every existing document and every existing site
    #    uses. It has to keep working exactly as it did.
    Invoke-Msi "install-with-an-address" @("/i", "`"$Msi`"", "ADLURL=$Address")
    $installed = $true

    if ((Read-Address) -ne $Address) {
        throw "The installer was given $Address and wrote '$(Read-Address)'."
    }

    Assert-ServiceRunning "The 'ADL Agent' service is not running after a fresh install."

    Write-Host "    agent.ini says $(Read-Address)" -ForegroundColor Green

    # And the window is findable. Read off disk rather than out of the
    # package, because a shortcut whose folder property resolved somewhere
    # unexpected still installs cleanly and still leaves a technician with
    # nothing to click.
    foreach ($shortcut in $shortcutPaths) {
        if (-not (Test-Path $shortcut)) {
            throw "There is no $shortcut, so a finished install left nothing to open the window with."
        }
    }

    Write-Host "    shortcuts installed in all three places" -ForegroundColor Green

    # 2. The same sources at the next version, which is what a release is and
    #    what UpdateService hands to msiexec.
    Write-Host "==> Building $upgradeMsi to upgrade with" -ForegroundColor Cyan

    & (Join-Path $PSScriptRoot "build-msi.ps1") `
        -Version $upgradeVersion `
        -Output $upgradeMsi `
        -ServiceDirectory $serviceDir `
        -TrayDirectory $trayDir `
        -AssetsDirectory $assets

    # 3. The self-update, exactly as UpdateService performs it: silent, and
    #    passed nothing. If this leaves the machine not knowing where to
    #    report, every machine in every fleet loses its address the next time
    #    it updates itself.
    Invoke-Msi "upgrade-with-nothing" @("/i", "`"$upgradeMsi`"")

    $after = Read-Address

    if ($after -ne $Address) {
        throw "A silent upgrade passed no properties changed the address from $Address to '$after'."
    }

    Assert-ServiceRunning "The 'ADL Agent' service is not running after a silent upgrade."

    Write-Host "    agent.ini still says $after" -ForegroundColor Green
    Write-Host "==> The package installs, configures itself, and upgrades without forgetting." -ForegroundColor Green

    # Only now, so that the check in the tidy-up below cannot turn a failure
    # above into a second, misleading one.
    $verified = $true
}
finally {
    if ($installed) {
        Write-Host "==> Removing it again" -ForegroundColor Cyan

        foreach ($package in @($upgradeMsi, $Msi)) {
            if (Test-Path $package) {
                # SilentlyContinue: only one of the two products is installed
                # by the time this runs, and a tidy-up that failed the build it
                # was tidying up after would be its own false alarm.
                Start-Process -FilePath "msiexec.exe" -Wait `
                    -ArgumentList @("/x", "`"$package`"", "/qn", "/norestart") `
                    -ErrorAction SilentlyContinue
            }
        }

        # The state directory is a permanent component, on purpose: a major
        # upgrade must not take the device token with it. That makes removing
        # it this script's job.
        Remove-Item (Join-Path $env:ProgramData "ADL Agent") -Recurse -Force -ErrorAction SilentlyContinue

        # Nothing a shortcut lives in is permanent, so the uninstall above
        # should have taken all three away. A desktop icon left pointing at a
        # program that is no longer there is one somebody clicks for the rest
        # of the machine's life. Checked only when everything above passed, so
        # that a tidy-up cannot report a second failure over the real one.
        if ($verified) {
            $left = @($shortcutPaths | Where-Object { Test-Path $_ })

            if ($left.Count -ne 0) {
                throw "Uninstalling left shortcuts behind: $($left -join ', ')."
            }

            Write-Host "==> And removing it leaves no shortcut behind." -ForegroundColor Green
        }
    }
}

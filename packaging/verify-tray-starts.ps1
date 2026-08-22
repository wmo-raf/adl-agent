<#
.SYNOPSIS
    Prove the published tray actually starts on this machine.

.DESCRIPTION
    Building the tray and testing it prove nothing about whether Windows will
    run it. Its manifest is not read by the compiler, its window is
    deliberately not automated, and the packaging job builds an installer
    around a program it never launches -- so a tray that cannot start at all
    looks exactly like a healthy build, right up to the moment somebody
    installs it on a country server.

    That is not hypothetical: a manifest missing its <assemblyIdentity>
    shipped, and Windows refused the program with "the side-by-side
    configuration is incorrect" before a line of it ran. Nothing in this
    repository could have known.

    So: start it, and wait to be told it is running by the program itself.

    "Still alive" alone is too weak a signal to trust. A loader failure can
    put a modal error box on an interactive desktop and leave the process
    sitting behind it, which looks like health to anything counting
    processes. The binding trace is the program's own first word -- written
    from managed code in OnStartup, before the first window is constructed --
    so a header in that file means the runtime came up, the bundle unpacked,
    the manifest was accepted and this application's own code is running.

    The trace is then printed rather than asserted on. An empty file past its
    header means every binding in the window resolved; WPF also reports
    transient warnings that resolve themselves a moment later, and a
    packaging job that fails the build over those would teach people to
    ignore it. Somebody reading the log is worth more here than a rule
    nobody trusts.

    The service is not checked here. It declares no manifest of its own, and
    unlike the tray it cannot be started harmlessly on a machine with no ADL
    to talk to.

.PARAMETER PublishDirectory
    Where `pack.ps1` put its publish output.

.EXAMPLE
    ./packaging/verify-tray-starts.ps1
#>
[CmdletBinding()]
param(
    [string] $PublishDirectory = "publish",

    # Long enough for a cold self-contained single-file bundle to unpack on a
    # slow CI disk, and short enough that a program which is never going to
    # start does not hold the build for minutes.
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"

$repository = Split-Path -Parent $PSScriptRoot
$tray = Join-Path $repository (Join-Path $PublishDirectory "tray/adl-agent-tray.exe")

if (-not (Test-Path $tray)) {
    throw "There is no tray to start at $tray. Run packaging/pack.ps1 first."
}

$log = Join-Path ([System.IO.Path]::GetTempPath()) "adl-agent-tray-bindings.log"
Remove-Item $log -ErrorAction SilentlyContinue

# The window has no way of knowing it is being watched. This is the same
# switch a person debugging the tray turns on by hand (see the README), used
# here for the one thing it says before it says anything else: I am running.
$env:ADL_AGENT_TRAY_BINDING_LOG = $log

Write-Host "==> Starting $tray" -ForegroundColor Cyan

# When Windows refuses to create the process at all, the exception says only
# that it refused. The reason -- which manifest, which line, which assembly it
# could not find -- goes to the Application event log under the SideBySide
# provider, and is the whole of what anybody debugging this needs. Reading it
# here is what turns "it does not start" into something actionable from a CI
# log by somebody who cannot get to the machine.
$askedAt = Get-Date

try {
    $trayProcess = Start-Process -FilePath $tray -PassThru
}
catch {
    Write-Host "==> Windows would not start it:" -ForegroundColor Red
    Write-Host $_.Exception.Message

    Write-Host "==> What the SideBySide provider recorded:" -ForegroundColor Cyan

    $sxs = Get-WinEvent -ErrorAction SilentlyContinue -FilterHashtable @{
        LogName = "Application"
        ProviderName = "SideBySide"
        StartTime = $askedAt.AddMinutes(-2)
    }

    if ($sxs) {
        $sxs | Select-Object -First 5 | ForEach-Object {
            Write-Host "--- $($_.TimeCreated) ---"
            Write-Host $_.Message
        }
    }
    else {
        Write-Host "Nothing, which is itself informative: activation context generation did not fail, so the refusal came from somewhere else."
    }

    throw
}

try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $started = $false

    while ((Get-Date) -lt $deadline) {
        if ($trayProcess.HasExited) {
            $code = $trayProcess.ExitCode

            # The codes worth naming, because they are the ones that mean
            # "Windows refused the program" rather than "the program decided
            # to stop", and they are otherwise eight opaque hex digits.
            $hint = switch ($code) {
                -1073741502 { " (0xC0000142 STATUS_DLL_INIT_FAILED)" }
                -1072365566 { " (0xC0150002 STATUS_SXS_CANT_GEN_ACTCTX -- the manifest)" }
                -1072365564 { " (0xC0150004 STATUS_SXS_ASSEMBLY_NOT_FOUND)" }
                default     { "" }
            }

            throw "The tray exited with 0x$('{0:X8}' -f $code)$hint before it started. " +
                  "Windows records the reason in the Application event log under the source SideBySide."
        }

        $said = Get-Content $log -Raw -ErrorAction SilentlyContinue

        if ($said -and $said.Trim()) {
            $started = $true
            break
        }

        Start-Sleep -Milliseconds 250
    }

    if (-not $started) {
        # Which of the two silences this is. A file that is missing means the
        # program never reached OnStartup; a file that exists and is empty
        # means it did and the write has not landed. They call for opposite
        # investigations, and guessing between them costs a round trip.
        if (Test-Path $log) {
            Write-Host "The trace file exists and holds $((Get-Item $log).Length) bytes." -ForegroundColor Yellow
        }
        else {
            Write-Host "The trace file was never created." -ForegroundColor Yellow
        }

        Write-Host "The process is responding: $($trayProcess.Responding)" -ForegroundColor Yellow

        throw "The tray is still running after $TimeoutSeconds seconds but has said nothing."
    }

    Write-Host "==> It started." -ForegroundColor Green

    # A moment more before reading the trace, so the window is built and its
    # bindings evaluated: the header alone only proves the process reached
    # managed code, and what a person reads this log for is what came after
    # it.
    Start-Sleep -Seconds 5

    Write-Host "==> Binding trace" -ForegroundColor Cyan
    Get-Content $log
}
finally {
    if (-not $trayProcess.HasExited) {
        # SilentlyContinue: it may have exited between the question and
        # the answer, and a tidy-up that fails the build it was tidying
        # up after would be its own kind of false alarm.
        Stop-Process -Id $trayProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

@echo off
rem Build the installers on this machine.
rem
rem The counterpart to run.cmd: that one starts a build the Mac made, this
rem one makes the build the Mac cannot. The WiX toolset and Velopack are
rem Windows-only, so the installers are made here, from the source
rem dev/sync-source.sh mirrors into src-mirror\.
rem
rem It runs packaging\pack.ps1 -- the same script CI runs and the same one
rem a release is reproduced with -- so what comes out of this is what a
rem country installs, at a version of your choosing -- bare, with no
rem switches, exactly as CI runs it.
rem
rem   pack.cmd                 the MSI, at the version below
rem   pack.cmd 0.3.1           at that one
rem   pack.cmd 0.3.1 --both    the per-user tier as well (slower, and
rem                            nothing ships it -- see the README)

setlocal

set "ROOT=%~dp0"
set "SRC=%ROOT%src-mirror"
set "OUT=%ROOT%pkg"

rem Three numbers, because Windows Installer compares only three fields of a
rem product version and the update feed refuses anything else. Bump it by
rem hand when you want two packages side by side to upgrade one with the
rem other; pack.ps1 allows a same-version reinstall, so leaving it alone is
rem fine for everything else.
set "VERSION=%~1"
if not defined VERSION set "VERSION=0.3.0"

set "TIERS="
if /i "%~2"=="--both" set "TIERS=-WithPerUserTier"

if not exist "%SRC%\packaging\pack.ps1" (
    echo.
    echo There is no source in %SRC%. Run dev/sync-source.sh on the Mac first.
    echo.
    pause
    exit /b 1
)

rem `where dotnet` is not the question. The .NET Desktop Runtime this machine
rem already has for run.cmd puts dotnet.exe on the PATH, and it answers --
rem then says "No .NET SDKs were found" the moment anything asks it to build.
rem So ask for what is actually needed: an SDK it will list.
set "DOTNET_DIR="
for /f "delims=" %%s in ('dotnet --list-sdks 2^>nul') do set "DOTNET_DIR=on the PATH"

if defined DOTNET_DIR goto sdk_ok

rem None there. Look in the other roots before giving up, because
rem "installed but invisible" is the ordinary case rather than the exotic
rem one. .NET 7 removed multi-level lookup, so an x64 dotnet cannot see an
rem ARM64 SDK or the other way round, and a machine that installed the x64
rem Desktop Runtime for run.cmd and then let winget choose the SDK's
rem architecture ends up with one of each, in two folders, each blind to
rem the other.
rem
rem A folder rather than a `--list-sdks` from each: a runtime-only install
rem has no sdk\ at all, and testing for it needs no process started and no
rem quoted path passed through for /f, which is its own small minefield.
rem
rem In a variable first because the x86 one has brackets in it, and a bare
rem %ProgramFiles(x86)% inside a parenthesised list is how a batch file
rem starts reading its own punctuation as syntax.
set "PF86=%ProgramFiles(x86)%"

for %%d in (
    "%ProgramFiles%\dotnet"
    "%ProgramFiles%\dotnet\x64"
    "%PF86%\dotnet"
    "%LocalAppData%\Microsoft\dotnet"
) do (
    if not defined DOTNET_DIR if exist "%%~d\sdk\" set "DOTNET_DIR=%%~d"
)

if defined DOTNET_DIR goto sdk_elsewhere

echo.
echo There is no .NET SDK this machine can see -- only a runtime. The
echo runtime is what run.cmd needs; publishing the two programs and
echo installing the WiX toolset both need the SDK:
echo.
echo     winget install Microsoft.DotNet.SDK.10 --architecture x64
echo.
echo or https://dotnet.microsoft.com/download/dotnet/10.0 -- the SDK
echo installer, not the runtime. global.json asks for 10.0.100 and rolls
echo forward, so any 10.0.x SDK will do.
echo.
echo If winget says it is already installed, then it is installed somewhere
echo neither the PATH nor this script looked. Compare:
echo.
echo     where dotnet
echo     dotnet --info
echo     dir "%%ProgramFiles%%\dotnet\sdk"
echo     dir "%%ProgramFiles%%\dotnet\x64\sdk"
echo.
echo Then close this window and open a new one, so a new PATH is picked up.
echo.
pause
exit /b 1

:sdk_elsewhere
rem Found one the PATH does not lead to. Say so rather than quietly
rem enjoying it: every other dotnet command on this machine is still going
rem to the wrong one, and a build that works only when launched from here
rem is worth knowing about.
echo Using the SDK in %DOTNET_DIR%, which is not where `dotnet` resolves to.
echo Putting that folder ahead of the other in the system PATH would fix
echo this everywhere rather than only here.
echo.
set "DOTNET_ROOT=%DOTNET_DIR%"
set "PATH=%DOTNET_DIR%;%PATH%"

:sdk_ok

rem Windows locks a running program's files, and a self-contained publish
rem overwrites both of them. The console loop from run.cmd is the usual
rem culprit; an installed service holds its own copy elsewhere and does not
rem matter here.
echo Stopping anything still running...
taskkill /IM adl-agent-tray.exe /F >nul 2>&1
taskkill /IM adl-agent.exe /F >nul 2>&1

rem A tool installed by pack.ps1 lands on the PATH of a process that has
rem already started, so the first run after a fresh SDK install would not
rem see wix. Adding it up front costs nothing and saves that one confusing
rem failure.
set "PATH=%PATH%;%USERPROFILE%\.dotnet\tools"

echo Building version %VERSION% %TIERS%
echo.

powershell -NoProfile -ExecutionPolicy Bypass ^
    -File "%SRC%\packaging\pack.ps1" ^
    -Version %VERSION% ^
    -OutputDirectory "%OUT%" ^
    %TIERS%

if errorlevel 1 (
    echo.
    echo The build failed. The output above is the whole of what went wrong.
    echo.
    pause
    exit /b 1
)

echo.
echo In %OUT%:
echo.
echo   double-click AdlAgent-%VERSION%-x64.msi     the screen nothing else tests
echo   msiexec /i "%OUT%\AdlAgent-%VERSION%-x64.msi" ADLURL=https://adl.example.org
echo   msiexec /i "%OUT%\AdlAgent-%VERSION%-x64.msi" /qn    what a self-update is
echo.
echo An installed agent runs as a service, on its own, and keeps its state
echo in %%ProgramData%%\ADL Agent. Close run.cmd's windows before trying one:
echo two agents against one ADL is two of everything, and they contend for
echo the same named pipe, so `adl-agent status` answers from whichever got
echo there first.
echo.
echo   msiexec /x "%OUT%\AdlAgent-%VERSION%-x64.msi"        when you want the loop back
echo.
pause

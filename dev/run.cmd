@echo off
rem Start a freshly-deployed agent and tray on this machine.
rem
rem Console processes, not the installed service: Windows locks a running
rem service's binaries, and stopping one needs administrator rights every
rem time round the loop. Started this way the agent runs as you, in your
rem session, with its log in a window you can read.
rem
rem Lives next to bin\ and state\ on the shared folder, so the Mac's
rem dev/deploy.sh replaces the program underneath it and nothing else moves.
rem
rem   run.cmd            against the ADL in dev.local.cmd
rem   run.cmd --no-url   deliberately unconfigured (see below)

setlocal

set "ROOT=%~dp0"
set "BIN=%ROOT%bin"
set "STATE=%ROOT%state"

rem Which ADL this machine talks to. Kept in a file of its own, and out of
rem the repository, because it is this machine's business rather than the
rem project's.
if exist "%ROOT%dev.local.cmd" call "%ROOT%dev.local.cmd"

rem An unconfigured machine is a state worth being able to reach on purpose.
rem It is what a scripted install that omitted the property leaves behind,
rem and it is what wmo-raf/adl#294 is about. A loop that refused to start
rem without an address could not reproduce the one state that issue exists
rem to fix, so --no-url starts the agent with none.
set "NOURL="
if /i "%~1"=="--no-url" set "NOURL=1"

if not defined ADL_URL if not defined NOURL (
    echo.
    echo No ADL_URL. Create dev.local.cmd next to this file holding one line:
    echo.
    echo     set ADL_URL=https://adl.example.org
    echo.
    echo It must be https. The agent refuses plain HTTP to anywhere but this
    echo machine, because the device token travels on every call.
    echo.
    echo To start deliberately unconfigured instead:  run.cmd --no-url
    echo.
    pause
    exit /b 1
)

if not exist "%BIN%\adl-agent.exe" (
    echo.
    echo There is no build in %BIN%. Run dev/deploy.sh on the Mac first.
    echo.
    pause
    exit /b 1
)

rem Whatever is still up from the last time round. Ignore the failures:
rem "not running" is the ordinary case, not a problem.
echo Stopping anything still running...
taskkill /IM adl-agent-tray.exe /F >nul 2>&1
taskkill /IM adl-agent.exe /F >nul 2>&1

if not exist "%STATE%" mkdir "%STATE%"

rem A WPF binding whose path is wrong neither throws nor draws, and an empty
rem label looks exactly like one the agent did not fill in. This is the
rem tray's own switch for saying which it was -- see the README. Empty past
rem its header means every binding in the window resolved.
set "ADL_AGENT_TRAY_BINDING_LOG=%ROOT%tray-bindings.log"
del "%ADL_AGENT_TRAY_BINDING_LOG%" >nul 2>&1

rem /k rather than /c: when it exits, the window and its last words stay.
rem /s with it: the program path and each argument are quoted, and /s is what
rem tells cmd to strip the outer pair and take the rest literally rather than
rem guessing where the command ends.
if defined NOURL (
    echo Starting the agent with NO ADL address configured
    start "ADL Agent [unconfigured]" cmd /s /k ""%BIN%\adl-agent.exe" --Agent:StateDirectory="%STATE%""
) else (
    echo Starting the agent against %ADL_URL%
    start "ADL Agent" cmd /s /k ""%BIN%\adl-agent.exe" --Agent:AdlBaseUrl="%ADL_URL%" --Agent:StateDirectory="%STATE%""
)

rem The tray connects to the agent's named pipe as it opens, and a tray that
rem starts first simply shows the agent as not running until it retries.
timeout /t 3 /nobreak >nul

echo Starting the tray
start "" "%BIN%\adl-agent-tray.exe"

echo.
echo Both started. To pair this machine, in any terminal:
echo     "%BIN%\adl-agent.exe" pair XXXX-XXXX
echo     "%BIN%\adl-agent.exe" status
echo.
echo Pairing is kept in %STATE% and survives a redeploy.
echo Delete that folder to start again unpaired.
echo.

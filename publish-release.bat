@echo off
REM Builds Release, publishes the self-contained artifact, and gates on the world audit.
REM Run before every commit and push so the artifact never drifts from the pushed source.
setlocal

pushd "%~dp0"

set OUT=artifacts\Driftwood-win-x64

echo [1/3] building Release...
dotnet build Driftwood.sln -c Release -v quiet --nologo
if errorlevel 1 goto :failed_build

echo [2/3] publishing self-contained win-x64...
dotnet publish src\Driftwood.Client\Driftwood.Client.csproj -c Release -r win-x64 --self-contained true -o "%OUT%" -v quiet --nologo
if errorlevel 1 goto :failed_publish

REM Gate on the published binary, not the build output: this is the thing that ships, and it
REM catches a publish that dropped a native dependency the build had sitting beside it.
echo [3/4] auditing the published build...
"%OUT%\Driftwood.exe" --audit --seed driftwood --chunks 12
if errorlevel 1 goto :failed_audit

REM Opens a window for about two seconds and reads its own pixels back off the framebuffer.
REM The audit runs headless and cannot see the screen at all, and the overlay spent its whole
REM life being back-face culled: built correctly, submitted correctly, no GL error reported, and
REM never once drawn. Every check in the project passed throughout. Nothing but this catches it.
echo [4/4] checking the overlay reaches the screen...
"%OUT%\Driftwood.exe" --ui-check --chunks 6 --seed driftwood
if errorlevel 1 goto :failed_ui

echo.
echo OK  %OUT%\Driftwood.exe
popd
exit /b 0

:failed_ui
echo.
echo UI CHECK FAILED - the overlay is not reaching the screen. See the faults above.
popd
exit /b 1

:failed_build
echo.
echo BUILD FAILED. A running Driftwood.exe locks its own file - close it and retry if there were no compiler errors.
popd
exit /b 1

:failed_publish
echo.
echo PUBLISH FAILED.
popd
exit /b 1

:failed_audit
echo.
echo AUDIT FAILED - not shipping this build. See the failed checks above.
popd
exit /b 1

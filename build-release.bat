@echo off
REM Builds Driftwood in Release. Release is the only configuration we verify against.
setlocal

pushd "%~dp0"

dotnet build Driftwood.sln -c Release --nologo
set BUILD_EXIT=%ERRORLEVEL%

popd

if %BUILD_EXIT% neq 0 (
    echo.
    echo BUILD FAILED ^(exit %BUILD_EXIT%^)
    echo A running Driftwood.exe locks its own file - close it and retry if there were no compiler errors.
    exit /b %BUILD_EXIT%
)

echo.
echo Built: src\Driftwood.Client\bin\Release\net11.0\Driftwood.exe
exit /b 0

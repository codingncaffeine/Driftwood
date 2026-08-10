@echo off
REM Builds Release, publishes one self-contained executable, and runs every release gate.
REM Run before every commit and push so the artifact never drifts from the pushed source.
setlocal

pushd "%~dp0"

set "OUT=artifacts\Driftwood-win-x64"
for /f "delims=" %%V in ('dotnet msbuild src\Driftwood.Client\Driftwood.Client.csproj -getProperty:Version -nologo') do set "VERSION=%%V"
if not defined VERSION goto :failed_version
set "PACKAGE=artifacts\Driftwood-v%VERSION%-win-x64.zip"
set "CHECKSUM=artifacts\Driftwood-v%VERSION%-win-x64.sha256"

echo [1/6] building Release...
dotnet build Driftwood.sln -c Release -v quiet --nologo
if errorlevel 1 goto :failed_build

REM dotnet publish does not clear its output directory. Validate this exact generated directory and
REM remove it first so an old loose asset can never hitch a ride in a later release.
echo [2/6] publishing one self-contained win-x64 executable...
powershell.exe -NoProfile -Command ^
  "$workspace = [IO.Path]::GetFullPath((Get-Location).Path);" ^
  "$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'));" ^
  "$target = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts\Driftwood-win-x64'));" ^
  "if ([IO.Path]::GetDirectoryName($target) -ne $artifactRoot -or [IO.Path]::GetFileName($target) -ne 'Driftwood-win-x64') { throw 'Refusing to clean an unexpected release path.' };" ^
  "if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }"
if errorlevel 1 goto :failed_publish

dotnet publish src\Driftwood.Client\Driftwood.Client.csproj -c Release -r win-x64 --self-contained true -o "%OUT%" -v quiet --nologo -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=embedded
if errorlevel 1 goto :failed_publish

REM Gate on the published binary, not the build output: this is the thing that ships, and it
REM catches a publish that dropped a native dependency the build had sitting beside it.
echo [3/6] checking release identity and embedded offline audio...
"%OUT%\Driftwood.exe" --version
"%OUT%\Driftwood.exe" --version | findstr.exe /x /c:"Driftwood v%VERSION%" >nul
if errorlevel 1 goto :failed_version
"%OUT%\Driftwood.exe" --audio-check
if errorlevel 1 goto :failed_audio

echo [4/6] auditing the published build...
"%OUT%\Driftwood.exe" --audit --seed driftwood --chunks 12
if errorlevel 1 goto :failed_audit

REM Opens a window for about two seconds and reads its own pixels back off the framebuffer.
REM The audit runs headless and cannot see the screen at all, and the overlay spent its whole
REM life being back-face culled: built correctly, submitted correctly, no GL error reported, and
REM never once drawn. Every check in the project passed throughout. Nothing but this catches it.
echo [5/6] checking every interface reaches the screen...
"%OUT%\Driftwood.exe" --ui-check --chunks 6 --seed driftwood
if errorlevel 1 goto :failed_ui

REM The public asset is a versioned ZIP containing exactly the gated executable, plus a checksum
REM beside it. Validate both paths and the ZIP entry so a stale or loose file cannot become payload.
echo [6/6] packaging the public release asset...
powershell.exe -NoProfile -Command ^
  "try {" ^
  "$ErrorActionPreference = 'Stop';" ^
  "$workspace = [IO.Path]::GetFullPath((Get-Location).Path);" ^
  "$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'));" ^
  "$out = [IO.Path]::GetFullPath((Join-Path $workspace '%OUT%'));" ^
  "$package = [IO.Path]::GetFullPath((Join-Path $workspace '%PACKAGE%'));" ^
  "$checksum = [IO.Path]::GetFullPath((Join-Path $workspace '%CHECKSUM%'));" ^
  "if ([IO.Path]::GetDirectoryName($package) -ne $artifactRoot -or [IO.Path]::GetDirectoryName($checksum) -ne $artifactRoot) { throw 'Refusing to write outside the artifact root.' };" ^
  "$payload = @(Get-ChildItem -LiteralPath $out -Force);" ^
  "if ($payload.Count -ne 1 -or $payload[0].PSIsContainer -or $payload[0].Name -ne 'Driftwood.exe') { throw 'The publish output is not exactly one Driftwood.exe.' };" ^
  "if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force };" ^
  "if (Test-Path -LiteralPath $checksum) { Remove-Item -LiteralPath $checksum -Force };" ^
  "Add-Type -AssemblyName System.IO.Compression;" ^
  "Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
  "$archive = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create);" ^
  "try { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $payload[0].FullName, 'Driftwood.exe', [IO.Compression.CompressionLevel]::Optimal) | Out-Null } finally { $archive.Dispose() };" ^
  "$archive = [IO.Compression.ZipFile]::OpenRead($package);" ^
  "try { if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -ne 'Driftwood.exe') { throw 'The release ZIP does not contain exactly Driftwood.exe.' } } finally { $archive.Dispose() };" ^
  "$sha256 = [Security.Cryptography.SHA256]::Create();" ^
  "$packageStream = [IO.File]::OpenRead($package);" ^
  "try { $hash = [BitConverter]::ToString($sha256.ComputeHash($packageStream)).Replace('-', '').ToLowerInvariant() } finally { $packageStream.Dispose(); $sha256.Dispose() };" ^
  "[IO.File]::WriteAllText($checksum, ($hash + '  ' + [IO.Path]::GetFileName($package) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false));" ^
  "} catch { [Console]::Error.WriteLine($_); exit 1 }"
if errorlevel 1 goto :failed_package

echo.
echo OK  %PACKAGE%
echo     %CHECKSUM%
popd
exit /b 0

:failed_package
echo.
echo PACKAGE FAILED - the public ZIP or checksum was not produced safely.
popd
exit /b 1

:failed_version
echo.
echo VERSION FAILED - Directory.Build.props, the executable and the package name do not agree.
popd
exit /b 1

:failed_ui
echo.
echo UI CHECK FAILED - an interface is not reaching the screen. See the faults above.
popd
exit /b 1

:failed_audio
echo.
echo AUDIO CHECK FAILED - the executable cannot decode its offline fallback. See the faults above.
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

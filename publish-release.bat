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

echo [1/7] building Release...
dotnet build Driftwood.sln -c Release -v quiet --nologo
if errorlevel 1 goto :failed_build

REM dotnet publish does not clear its output directory. Validate this exact generated directory and
REM remove it first so an old loose asset can never hitch a ride in a later release.
echo [2/7] publishing one self-contained win-x64 executable...
powershell.exe -NoProfile -Command ^
  "$workspace = [IO.Path]::GetFullPath((Get-Location).Path);" ^
  "$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'));" ^
  "$target = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts\Driftwood-win-x64'));" ^
  "if ([IO.Path]::GetDirectoryName($target) -ne $artifactRoot -or [IO.Path]::GetFileName($target) -ne 'Driftwood-win-x64') { throw 'Refusing to clean an unexpected release path.' };" ^
  "if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }"
if errorlevel 1 goto :failed_publish

dotnet publish src\Driftwood.Client\Driftwood.Client.csproj -c Release -r win-x64 --self-contained true -o "%OUT%" -v quiet --nologo -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=embedded
if errorlevel 1 goto :failed_publish

REM The public app must be a Windows GUI executable so a normal launch never opens a console. Its
REM explicit tools attach to the caller instead; the captured --version check below proves that half.
powershell.exe -NoProfile -Command ^
  "$bytes = [IO.File]::ReadAllBytes((Join-Path (Get-Location) '%OUT%\Driftwood.exe'));" ^
  "$pe = [BitConverter]::ToInt32($bytes, 0x3c);" ^
  "$subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68);" ^
  "if ($subsystem -ne 2) { throw ('Driftwood.exe uses PE subsystem ' + $subsystem + ', not Windows GUI (2).') }"
if errorlevel 1 goto :failed_publish

REM Gate on the published binary, not the build output: this is the thing that ships, and it
REM catches a publish that dropped a native dependency the build had sitting beside it.
echo [3/7] checking release identity, embedded offline audio and P10.5 magic...
set "DRIFTWOOD_EXPECT=Driftwood v%VERSION%"
call :run_windowed --version
set "DRIFTWOOD_EXPECT="
if errorlevel 1 goto :failed_version
call :run_windowed --audio-check
if errorlevel 1 goto :failed_audio
call :run_windowed --magic-check
if errorlevel 1 goto :failed_magic

REM SDL is a bundled native dependency and controllers are optional hardware. This requires the
REM former to load from the single EXE while explicitly allowing zero of the latter.
echo [4/7] checking bundled SDL3 and controller interop...
call :run_windowed --controller-check
if errorlevel 1 goto :failed_controller

echo [5/7] auditing the published build...
call :run_windowed --audit --seed driftwood --chunks 12
if errorlevel 1 goto :failed_audit

REM Opens a window for about two seconds and reads its own pixels back off the framebuffer.
REM The audit runs headless and cannot see the screen at all, and the overlay spent its whole
REM life being back-face culled: built correctly, submitted correctly, no GL error reported, and
REM never once drawn. Every check in the project passed throughout. Nothing but this catches it.
echo [6/7] checking every interface reaches the screen...
call :run_windowed --ui-check --chunks 6 --seed driftwood --width 400 --height 480
if errorlevel 1 goto :failed_ui
call :run_windowed --ui-check --chunks 6 --seed driftwood --width 1600 --height 900
if errorlevel 1 goto :failed_ui
call :run_windowed --ui-check --chunks 6 --seed driftwood --width 1920 --height 1440
if errorlevel 1 goto :failed_ui

REM The public asset is a versioned ZIP containing exactly the gated executable, plus a checksum
REM beside it. Validate both paths and the ZIP entry so a stale or loose file cannot become payload.
echo [7/7] packaging the public release asset...
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

REM A GUI-subsystem process does not make cmd.exe wait for it. Run explicit instruments through a
REM console parent that waits and captures their output, or every release check races the next one.
:run_windowed
powershell.exe -NoProfile -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$exe = Join-Path (Get-Location) '%OUT%\Driftwood.exe';" ^
  "$stdout = Join-Path ([IO.Path]::GetDirectoryName($exe)) '.release-gate.stdout';" ^
  "$stderr = Join-Path ([IO.Path]::GetDirectoryName($exe)) '.release-gate.stderr';" ^
  "$code = 1;" ^
  "try {" ^
  "$process = Start-Process -FilePath $exe -ArgumentList '%*' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr;" ^
  "$out = if (Test-Path -LiteralPath $stdout) { [IO.File]::ReadAllText($stdout) } else { '' };" ^
  "$err = if (Test-Path -LiteralPath $stderr) { [IO.File]::ReadAllText($stderr) } else { '' };" ^
  "if ($out.Length -gt 0) { [Console]::Out.Write($out) };" ^
  "if ($err.Length -gt 0) { [Console]::Error.Write($err) };" ^
  "$code = $process.ExitCode;" ^
  "if ($code -eq 0 -and $env:DRIFTWOOD_EXPECT -and $out.TrimEnd() -cne $env:DRIFTWOOD_EXPECT) { [Console]::Error.WriteLine('Expected exact output: ' + $env:DRIFTWOOD_EXPECT); $code = 1 }" ^
  "} catch { [Console]::Error.WriteLine($_); $code = 1 }" ^
  "finally { Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue };" ^
  "exit $code"
exit /b %errorlevel%

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

:failed_magic
echo.
echo MAGIC CHECK FAILED - P10.5 progression, spells, companions or persistence drifted.
popd
exit /b 1

:failed_controller
echo.
echo CONTROLLER CHECK FAILED - bundled SDL3 or the provider interop is unavailable. See the faults above.
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

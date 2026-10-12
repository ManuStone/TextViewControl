@echo off
rem Publishes the sample app with Native AOT: a native exe that needs no installed .NET runtime.
rem Usage: BuildAot.cmd [x64 ^| x86 ^| arm64]   (default x64)
rem Result: publish\win-<platform>\TextViewControlApp.exe
rem Needs the Visual Studio workload "Desktop development with C++" (linker),
rem arm64 additionally the component "C++ ARM64 build tools" (Visual Studio Installer, single components).
setlocal
cd /d "%~dp0"

set "PLATFORM=%~1"
if "%PLATFORM%"=="" set "PLATFORM=x64"
if /i not "%PLATFORM%"=="x64" if /i not "%PLATFORM%"=="x86" if /i not "%PLATFORM%"=="arm64" (
    echo Platform "%PLATFORM%" is not supported, use x64, x86 or arm64.
    pause
    exit /b 1
)

rem The Native AOT linker step looks for vswhere.exe in the PATH
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

dotnet publish TextViewControlApp\TextViewControlApp.csproj -c Release -p:PublishProfile=win-%PLATFORM% -o publish\win-%PLATFORM%
if errorlevel 1 (
    echo.
    echo Publish FAILED.
    pause
    exit /b 1
)

echo.
echo Done: %CD%\publish\win-%PLATFORM%\TextViewControlApp.exe
pause

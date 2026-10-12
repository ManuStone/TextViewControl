@echo off
rem Deletes all build output: bin, obj, publish, nupkg and .vs (close Visual Studio first,
rem it keeps files in .vs open; Visual Studio forgets open files and the startup project).
setlocal
cd /d "%~dp0"

for %%d in (TextViewControl TextViewControlApp) do (
    if exist "%%d\bin" rmdir /s /q "%%d\bin"
    if exist "%%d\obj" rmdir /s /q "%%d\obj"
)
if exist publish rmdir /s /q publish
if exist nupkg rmdir /s /q nupkg
if exist .vs rmdir /s /q .vs

echo Clean done.
pause

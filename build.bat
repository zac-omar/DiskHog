@echo off
rem Rebuilds DiskHog into the DiskHog\ folder (needs the .NET 10 SDK).
cd /d "%~dp0"
dotnet publish "src\DiskHog.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o "DiskHog"
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Built: %~dp0DiskHog\DiskHog.exe
pause

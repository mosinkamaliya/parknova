@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo .NET 8 SDK/runtime is not installed or not on PATH.
  echo Install .NET 8 SDK, then run this file again.
  pause
  exit /b 1
)
echo Starting ParkNova on http://localhost:5080 ...
start "ParkNova Browser" http://localhost:5080
dotnet run --project "%~dp0ParkNova.csproj" --urls "http://localhost:5080"
pause

@echo off
:loop
taskkill /F /IM ActivityApp.exe /T >nul 2>&1
timeout /t 3 /nobreak >nul
dotnet run --project "%~dp0ActivityApp.csproj" --configuration Release
timeout /t 3 /nobreak >nul
goto loop

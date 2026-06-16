@echo off
:loop
dotnet run --project "%~dp0ActivityApp.csproj" --configuration Release
timeout /t 2 /nobreak >nul
goto loop

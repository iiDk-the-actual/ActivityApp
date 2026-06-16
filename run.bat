@echo off
:loop
dotnet run --project "%~dp0ActivityApp.csproj" --configuration Release
timeout /t 10 /nobreak >nul
goto loop

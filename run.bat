@echo off
:loop
dotnet run --project "%~dp0" --configuration Release
timeout /t 2 /nobreak >nul
goto loop

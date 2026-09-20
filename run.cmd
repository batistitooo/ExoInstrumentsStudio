@echo off
rem ExoInstruments Studio on Windows, doing what run.sh does elsewhere: build the engine, then serve
rem the interface at http://127.0.0.1:5227 (or the port in PORT). Needs the .NET 10 SDK.
setlocal
cd /d "%~dp0"
if "%PORT%"=="" set "PORT=5227"
echo building...
dotnet build Engine\ExoStudio.csproj -v q --nologo || exit /b 1
dotnet run --project Engine\ExoStudio.csproj --no-build -- --port %PORT% %*

@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not exist "IdleShutdown.sln" (
    echo [ERROR] IdleShutdown.sln nebyl nalezen v: %CD%
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK neni nainstalovane nebo prikaz dotnet neni v PATH.
    echo         Nainstalujte .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

for /f "delims=" %%V in ('dotnet --version') do set "DOTNET_VERSION=%%V"
echo [INFO] Pouzivam .NET SDK: %DOTNET_VERSION%
echo [INFO] Cistim predchozi aplikacni build...

if exist "dist\Service" rmdir /s /q "dist\Service"
if exist "dist\Agent" rmdir /s /q "dist\Agent"
if not exist "dist\Service" mkdir "dist\Service"
if not exist "dist\Agent" mkdir "dist\Agent"

echo [INFO] Publikuji Windows sluzbu (win-x64)...
dotnet publish "src\IdleShutdown.Service\IdleShutdown.Service.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "dist\Service"
if errorlevel 1 goto :build_failed

echo [INFO] Publikuji uzivatelskeho agenta (win-x64)...
dotnet publish "src\IdleShutdown.Agent\IdleShutdown.Agent.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "dist\Agent"
if errorlevel 1 goto :build_failed

if exist "dist\config.json" del /q "dist\config.json"

if not exist "dist\Service\IdleShutdown.Service.exe" (
    echo [ERROR] Chybi dist\Service\IdleShutdown.Service.exe.
    exit /b 1
)

if not exist "dist\Agent\IdleShutdown.Agent.exe" (
    echo [ERROR] Chybi dist\Agent\IdleShutdown.Agent.exe.
    exit /b 1
)

echo [INFO] Vytvarim Chocolatey balicek...
dotnet pack "packaging\idle-shutdown\IdleShutdown.Package.csproj" -c Release --disable-build-servers
if errorlevel 1 goto :build_failed

if not exist "dist\package\idle-shutdown.nupkg" (
    echo [ERROR] Chybi dist\package\idle-shutdown.nupkg.
    exit /b 1
)

echo.
echo [OK] Build byl uspesne dokoncen.
echo [OK] Vystup: %CD%\dist
echo [OK] Chocolatey: %CD%\dist\package\idle-shutdown.nupkg
exit /b 0

:build_failed
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
if "%BUILD_EXIT_CODE%"=="0" set "BUILD_EXIT_CODE=1"
echo.
echo [ERROR] Build selhal s kodem %BUILD_EXIT_CODE%.
exit /b %BUILD_EXIT_CODE%

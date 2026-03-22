@echo off
title PC Guardian - Build
color 0A
echo.
echo   ========================================
echo       PC Guardian - Build to .EXE
echo   ========================================
echo.

cd /d "%~dp0"

where dotnet >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo   ERROR: .NET 8 SDK not found!
    echo.
    echo   Download it from:
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    echo   Install the SDK (not just Runtime), then run this again.
    echo.
    pause
    exit /b 1
)

echo   Restoring packages...
dotnet restore -q

echo   Building portable .exe (this may take a minute)...
echo.

dotnet publish -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o publish

echo.
if exist "publish\PCGuardian.exe" (
    echo   ========================================
    echo       BUILD SUCCESSFUL!
    echo   ========================================
    echo.
    echo   Your portable .exe is at:
    echo   %cd%\publish\PCGuardian.exe
    echo.
    echo   Copy it anywhere and double-click to run.
    echo   No installation needed!
) else (
    echo   Build failed. Check the errors above.
)

echo.
pause

@echo off
echo ==========================================
echo    VR Eye Test System Auto Launcher
echo ==========================================
echo [1] Connecting Oculus Communication Tunnel...
cd /d "%~dp0"
platform-tools\adb.exe reverse tcp:12346 tcp:12346
if %errorlevel% neq 0 (
    echo.
    echo [WARNING] Oculus might not be connected or screen is off!
) else (
    echo [SUCCESS] Communication Tunnel established.
)

echo.
echo [2] Opening Web Control Panel...
start http://127.0.0.1:12346

echo.
echo [3] Starting Backend Server... (Close this window to terminate server)
Eye_Server.exe
pause

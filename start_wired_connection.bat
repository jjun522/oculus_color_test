@echo off
echo ==========================================
echo [ Oculus USB Wired Connection Helper ]
echo ==========================================
echo.
echo Running 'adb reverse tcp:12346 tcp:12346'...
"C:\Users\user\Desktop\JAEJUN\eye\platform-tools\adb.exe" reverse tcp:12346 tcp:12346

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] ADB command failed!
    echo 1. Check if your Quest is connected via USB.
    echo 2. Make sure USB Debugging is enabled in Quest Developer settings.
    echo 3. Ensure ADB is installed and in your PATH.
) else (
    echo.
    echo [SUCCESS] Port forwarded successfully.
    echo Now your Quest can connect to '127.0.0.1:12346'.
)
echo.
pause

#!/bin/bash

# ==========================================
# [ Oculus USB Wired Connection Helper ]
# ==========================================

echo "Running 'adb reverse tcp:12346 tcp:12346'..."
adb reverse tcp:12346 tcp:12346

if [ $? -ne 0 ]; then
    echo ""
    echo "[ERROR] ADB command failed!"
    echo "1. Check if your Quest is connected via USB."
    echo "2. Make sure USB Debugging is enabled in Quest Developer settings."
    echo "3. Ensure ADB is installed and in your PATH."
else
    echo ""
    echo "[SUCCESS] Port 12346 forwarded successfully."
    echo "Now your Quest can connect to '127.0.0.1:12346'."
fi

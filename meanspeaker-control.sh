#!/bin/bash

# Quick MeanSpeaker management script
# Place this in your home directory for easy access

echo "🎵 MeanSpeaker Control Panel"
echo "=========================="
echo ""

# Check if service exists
if ! systemctl list-unit-files | grep -q meanspeaker.service; then
    echo "❌ MeanSpeaker service not installed."
    echo "Run ./setup-autostart.sh from the project directory first."
    exit 1
fi

# Show current status
echo "Current Status:"
if systemctl is-active --quiet meanspeaker; then
    echo "✅ MeanSpeaker is RUNNING"
else
    echo "⭕ MeanSpeaker is STOPPED"
fi

if systemctl is-enabled --quiet meanspeaker; then
    echo "🔄 Auto-start: ENABLED"
else
    echo "🚫 Auto-start: DISABLED"
fi

echo ""
echo "What would you like to do?"
echo "1) Start MeanSpeaker"
echo "2) Stop MeanSpeaker"
echo "3) Restart MeanSpeaker"
echo "4) View live logs"
echo "5) Enable auto-start on boot"
echo "6) Disable auto-start"
echo "7) Check detailed status"
echo "8) Exit"
echo ""

read -p "Enter choice (1-8): " choice

case $choice in
    1)
        echo "Starting MeanSpeaker..."
        sudo systemctl start meanspeaker
        echo "✅ Started!"
        ;;
    2)
        echo "Stopping MeanSpeaker..."
        sudo systemctl stop meanspeaker
        echo "⭕ Stopped!"
        ;;
    3)
        echo "Restarting MeanSpeaker..."
        sudo systemctl restart meanspeaker
        echo "🔄 Restarted!"
        ;;
    4)
        echo "Showing live logs (Ctrl+C to exit)..."
        sudo journalctl -u meanspeaker -f
        ;;
    5)
        echo "Enabling auto-start..."
        sudo systemctl enable meanspeaker
        echo "🔄 MeanSpeaker will now start automatically on boot!"
        ;;
    6)
        echo "Disabling auto-start..."
        sudo systemctl disable meanspeaker
        echo "🚫 Auto-start disabled."
        ;;
    7)
        echo "Detailed status:"
        sudo systemctl status meanspeaker
        ;;
    8)
        echo "Goodbye! 🎵"
        exit 0
        ;;
    *)
        echo "Invalid choice!"
        exit 1
        ;;
esac

#!/bin/bash

# Bluetooth Speaker Service Control Script

case "$1" in
    start)
        echo "🚀 Starting Bluetooth Speaker service..."
        sudo systemctl start bluetooth-speaker
        echo "✅ Service started!"
        ;;
    stop)
        echo "🛑 Stopping Bluetooth Speaker service..."
        sudo systemctl stop bluetooth-speaker
        echo "✅ Service stopped!"
        ;;
    restart)
        echo "🔄 Restarting Bluetooth Speaker service..."
        sudo systemctl restart bluetooth-speaker
        echo "✅ Service restarted!"
        ;;
    status)
        echo "📊 Bluetooth Speaker service status:"
        sudo systemctl status bluetooth-speaker --no-pager
        ;;
    logs)
        echo "📋 Bluetooth Speaker logs (Ctrl+C to exit):"
        sudo journalctl -u bluetooth-speaker -f
        ;;
    enable)
        echo "🔄 Enabling auto-start on boot..."
        sudo systemctl enable bluetooth-speaker
        echo "✅ Auto-start enabled!"
        ;;
    disable)
        echo "❌ Disabling auto-start on boot..."
        sudo systemctl disable bluetooth-speaker
        echo "✅ Auto-start disabled!"
        ;;
    install-api-key)
        echo "🔑 Setting up OpenAI API Key..."
        read -p "Enter your OpenAI API key: " api_key
        if [ ! -z "$api_key" ]; then
            echo "export OPENAI_API_KEY=\"$api_key\"" >> ~/.bashrc
            export OPENAI_API_KEY="$api_key"
            
            # Update the service to include the API key
            sudo sed -i "/Environment=PATH=/a Environment=OPENAI_API_KEY=$api_key" /etc/systemd/system/bluetooth-speaker.service
            sudo systemctl daemon-reload
            
            echo "✅ API key installed! Restart the service to apply: ./speaker-control.sh restart"
        else
            echo "❌ No API key provided"
        fi
        ;;
    manual)
        echo "🧪 Running manually (for testing)..."
        dotnet run
        ;;
    *)
        echo "🎵 Bluetooth Speaker Service Control"
        echo ""
        echo "Usage: $0 {start|stop|restart|status|logs|enable|disable|install-api-key|manual}"
        echo ""
        echo "Commands:"
        echo "  start           - Start the service now"
        echo "  stop            - Stop the service"
        echo "  restart         - Restart the service"
        echo "  status          - Show service status"
        echo "  logs            - Show live logs (Ctrl+C to exit)"
        echo "  enable          - Enable auto-start on boot"
        echo "  disable         - Disable auto-start on boot"
        echo "  install-api-key - Set up OpenAI API key"
        echo "  manual          - Run manually for testing"
        echo ""
        echo "Examples:"
        echo "  $0 start        # Start the speaker service"
        echo "  $0 logs         # Watch live logs"
        echo "  $0 status       # Check if it's running"
        echo ""
        exit 1
        ;;
esac

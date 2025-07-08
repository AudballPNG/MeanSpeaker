#!/bin/bash

# Simple run script for the Bluetooth Speaker
# This handles everything automatically!

echo "🎵 Starting Simple Snarky Bluetooth Speaker..."
echo ""
echo "This will automatically:"
echo "  ✅ Install required packages"
echo "  ✅ Configure Bluetooth services" 
echo "  ✅ Set up audio routing"
echo "  ✅ Make device discoverable as 'The Little Shit'"
echo "  ✅ Start monitoring for connections"
echo ""

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET 8 not found. Installing..."
    wget https://dot.net/v1/dotnet-install.sh
    chmod +x dotnet-install.sh
    ./dotnet-install.sh --channel 8.0
    export PATH=$PATH:$HOME/.dotnet
    echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
fi

# Check for API key
if [ -z "$OPENAI_API_KEY" ]; then
    echo "💡 Tip: Set OPENAI_API_KEY environment variable for AI commentary"
    echo "   Without it, you'll get simple fallback responses instead"
    echo ""
fi

# Run the program
echo "🚀 Starting Bluetooth Speaker..."
dotnet run "$@"

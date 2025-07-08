#!/bin/bash

# Fix Assembly Attributes Issue Script
# This script specifically addresses the duplicate assembly attributes error

echo "🔧 Fixing duplicate assembly attributes issue..."

# Step 1: Nuclear clean - remove everything
echo "Performing nuclear clean..."
dotnet clean
rm -rf obj/
rm -rf bin/
rm -rf ~/.nuget/packages/*/

# Step 2: Clear all NuGet caches
echo "Clearing NuGet caches..."
dotnet nuget locals all --clear

# Step 3: Remove any existing assembly attribute files
echo "Removing existing assembly attribute files..."
find . -name "*.AssemblyAttributes.cs" -delete 2>/dev/null || true
find . -name "*.AssemblyInfo.cs" -delete 2>/dev/null || true

# Step 4: Restore packages fresh
echo "Restoring packages..."
dotnet restore --no-cache

# Step 5: Build with specific settings
echo "Building with assembly info disabled..."
dotnet build --no-restore -p:GenerateAssemblyInfo=false

# Step 6: If still failing, try even more aggressive approach
if [ $? -ne 0 ]; then
    echo "Still failing. Trying with implicit usings disabled..."
    
    # Temporarily modify project file
    cp BluetoothSpeaker.csproj BluetoothSpeaker.csproj.backup
    
    # Create a minimal project file
    cat > BluetoothSpeaker.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <UseAppHost>true</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="9.0.6" />
    <PackageReference Include="Tmds.DBus" Version="0.21.2" />
  </ItemGroup>
</Project>
EOF

    # Clean and build again
    dotnet clean
    rm -rf obj/ bin/
    dotnet restore --no-cache
    dotnet build --no-restore
    
    # If it works, restore the original but keep the fix
    if [ $? -eq 0 ]; then
        echo "✅ Build successful with minimal project file!"
    else
        echo "❌ Still failing. Restoring original project file."
        mv BluetoothSpeaker.csproj.backup BluetoothSpeaker.csproj
    fi
fi

echo "Assembly attributes fix attempt completed."

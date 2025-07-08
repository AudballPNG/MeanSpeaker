# Quick Fix for Assembly Attributes Error

Run this single command in your MeanSpeaker directory:

```bash
dotnet clean && rm -rf obj/ bin/ && dotnet nuget locals all --clear && find . -name "*.AssemblyAttributes.cs" -delete && find . -name "*.AssemblyInfo.cs" -delete && dotnet restore --no-cache && dotnet build -p:GenerateAssemblyInfo=false
```

Or step by step:
```bash
dotnet clean
rm -rf obj/ bin/
dotnet nuget locals all --clear
find . -name "*.AssemblyAttributes.cs" -delete
find . -name "*.AssemblyInfo.cs" -delete
dotnet restore --no-cache
dotnet build -p:GenerateAssemblyInfo=false
```

If it builds successfully, then run:
```bash
dotnet run
```

# Copilot Instructions for Bluetooth Speaker Project

<!-- Use this file to provide workspace-specific custom instructions to Copilot. For more details, visit https://code.visualstudio.com/docs/copilot/copilot-customization#_use-a-githubcopilotinstructionsmd-file -->

## Project Overview
This is a Raspberry Pi Bluetooth speaker project that receives audio via Bluetooth A2DP and generates snarky AI commentary about music choices.

## Key Technologies
- .NET 8 console application
- BlueZ/D-Bus for Bluetooth management on Linux
- OpenAI API for generating music commentary
- Tmds.DBus library for D-Bus communication
- Cross-platform audio handling

## Project Goals
- Function as a Bluetooth A2DP audio sink
- Monitor music playback state and track information
- Generate humorous/snarky commentary about music choices using AI
- Provide simple console interface for control and status
- Optimize for Raspberry Pi deployment

## Code Style Guidelines
- Use async/await for all I/O operations
- Handle exceptions gracefully with appropriate logging
- Use dependency injection where appropriate
- Keep platform-specific code isolated in separate classes
- Prefer composition over inheritance
- Use descriptive variable and method names

## Architecture Notes
- Separate concerns: Bluetooth management, AI commentary, audio monitoring
- Use interfaces for testability and platform abstraction
- Implement proper resource disposal for audio and Bluetooth resources
- Design for easy configuration and deployment on Raspberry Pi

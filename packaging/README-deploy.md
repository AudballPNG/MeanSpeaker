MeanSpeaker deployment (Raspberry Pi)

Quick path to an auto-starting, shippable speaker service.

Requirements
- Raspberry Pi OS (systemd), Bluetooth + BlueALSA set up (use simple-setup.sh first)
- .NET 8 SDK on the build machine (can be on the Pi)

Steps
1) First-time system setup (one time):
   sudo ./simple-setup.sh

2) Publish and install service:
   cd packaging
   ./install-service.sh

3) Manage the service:
   sudo systemctl status meanspeaker
   sudo systemctl restart meanspeaker
   journalctl -u meanspeaker -f

Notes
- The service runs a self-contained single-file binary in /opt/meanspeaker.
- Default mode uses Local AI (Ollama + phi3:mini) when available.
- To use OpenAI mode, add Environment=OPENAI_API_KEY=... to the service file and restart.
- TTS defaults to Piper. Change ExecStart in the unit to switch engines.

Uninstall
   cd packaging
   ./uninstall-service.sh

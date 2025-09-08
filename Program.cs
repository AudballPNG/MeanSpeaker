namespace BluetoothSpeaker
{
    class Program
    {
        static void ShowHelp()
        {
            Console.WriteLine("🎵 Snarky Bluetooth Speaker - Usage:");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  --help, -h           Show this help message");
            Console.WriteLine("  --no-speech          Disable text-to-speech output");
            Console.WriteLine("  --openai-api         Use OpenAI API instead of local AI");
            Console.WriteLine("  --tts <engine>       Set TTS engine (piper, pico, festival, espeak)");
            Console.WriteLine("  --voice <voice>      Set voice (piper models or espeak voices)");
            Console.WriteLine();
            Console.WriteLine("TTS Engines:");
            Console.WriteLine("  piper     - Piper neural TTS (best quality, modern, default)");
            Console.WriteLine("  pico      - SVOX Pico TTS (good quality, reliable fallback)");
            Console.WriteLine("  festival  - Festival TTS (good quality)");
            Console.WriteLine("  espeak    - eSpeak TTS (lightweight, robotic)");
            Console.WriteLine();
            Console.WriteLine("Voice Options:");
            Console.WriteLine("  Piper voices: en_US-lessac-medium, en_US-ryan-medium, en_US-amy-medium");
            Console.WriteLine("  eSpeak voices: en+f3, en+m3, en+f4, en+m4 (female/male variants)");
            Console.WriteLine();
            Console.WriteLine("AI Modes:");
            Console.WriteLine("  Default              Local AI (Phi-3 Mini) - completely offline");
            Console.WriteLine("  --openai-api         Use OpenAI API (requires OPENAI_API_KEY)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  BluetoothSpeaker                           # Use local AI with Piper TTS");
            Console.WriteLine("  BluetoothSpeaker --openai-api              # Use OpenAI API");
            Console.WriteLine("  BluetoothSpeaker --tts espeak              # Use eSpeak TTS");
            Console.WriteLine("  BluetoothSpeaker --no-speech               # Text output only");
            Console.WriteLine();
            Console.WriteLine("Local AI Setup (Done automatically by simple-setup.sh):");
            Console.WriteLine("  1. Install Ollama: curl -fsSL https://ollama.ai/install.sh | sh");
            Console.WriteLine("  2. Start Ollama: ollama serve");
            Console.WriteLine("  3. Download model: ollama pull phi3:mini");
            Console.WriteLine("  4. Run speaker: BluetoothSpeaker");
            Console.WriteLine();
            Console.WriteLine("Environment Variables:");
            Console.WriteLine("  OPENAI_API_KEY       Your OpenAI API key (only for --openai-api mode)");
        }

        static async Task Main(string[] args)
        {
            // Self-install as a service on Linux with systemd (first run)
            try
            {
                var installed = await AutostartManager.EnsureAutostartAsync(args);
                if (installed)
                {
                    Console.WriteLine("🚀 Relaunching under systemd. Exiting foreground instance...");
                    return;
                }
            }
            catch { /* non-fatal */ }

            // Check for help
            if (args.Contains("--help") || args.Contains("-h"))
            {
                ShowHelp();
                return;
            }
            
            // Check for speech configuration  
            bool enableSpeech = !args.Contains("--no-speech");
            bool useOpenAI = args.Contains("--openai-api"); // OpenAI is now opt-in
            string ttsVoice = "en_US-lessac-medium"; // Default Piper voice
            string ttsEngine = "piper"; // Default to Piper TTS for best quality
            
            // Parse parameters
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--voice")
                {
                    ttsVoice = args[i + 1];
                }
                else if (args[i] == "--tts" || args[i] == "--tts-engine")
                {
                    ttsEngine = args[i + 1];
                    // Adjust default voice based on engine
                    if (ttsEngine == "espeak" && ttsVoice.StartsWith("en_US"))
                    {
                        ttsVoice = "en+f3"; // Default espeak voice
                    }
                }
            }
            
            Console.WriteLine("🎵 Snarky Bluetooth Speaker Starting Up...");
            Console.WriteLine($"Speech: {(enableSpeech ? "Enabled" : "Disabled")}");
            Console.WriteLine($"AI Mode: {(useOpenAI ? "OpenAI API" : "Local AI (Phi-3 Mini)")}");
            if (enableSpeech)
            {
                Console.WriteLine($"TTS Engine: {ttsEngine}");
                Console.WriteLine($"Voice: {ttsVoice}");
            }
            
            // Default to local AI mode (no API key needed)
            string apiKey = "local-ai-mode";
            
            if (useOpenAI)
            {
                // Get OpenAI API key from environment 
                string? envApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                
                if (!string.IsNullOrEmpty(envApiKey))
                {
                    Console.WriteLine("🌐 Using OpenAI API for commentary generation");
                    apiKey = envApiKey;
                }
                else
                {
                    Console.WriteLine("❌ OpenAI API key not found in environment.");
                    Console.WriteLine("💡 Falling back to local AI mode (recommended)");
                    apiKey = "local-ai-mode";
                }
            }
            else
            {
                Console.WriteLine("🤖 Local AI mode enabled - fully offline operation!");
            }
            
            using var monitor = new MusicMonitor(apiKey, enableSpeech, ttsVoice, ttsEngine);
            
            try
            {
                await monitor.InitializeAsync();
                // Speak readiness
                await monitor.AnnounceReadyAsync();
                await monitor.StartMonitoringAsync();
                
                Console.WriteLine("\n🎵 Snarky Bluetooth Speaker is running!");
                Console.WriteLine("Commands:");
                Console.WriteLine("  'quit' or 'exit' - Stop the application");
                Console.WriteLine("  'status' - Show current status");
                Console.WriteLine("  'test' - Generate a test comment");
                Console.WriteLine("  'debug' - Debug track detection");
                Console.WriteLine("  'sync' - Force sync track detection");
                Console.WriteLine("  'help' - Show startup help");
                Console.WriteLine("\nConnect your phone and start playing music to hear my commentary...\n");
                
                string? input;
                do
                {
                    input = Console.ReadLine()?.Trim().ToLower();
                    
                    switch (input)
                    {
                        case "status":
                            Console.WriteLine("📊 Checking status...");
                            await monitor.ShowStatusAsync();
                            break;
                        case "test":
                            Console.WriteLine("🧪 Testing commentary...");
                            await monitor.TestCommentAsync();
                            break;
                        case "debug":
                            Console.WriteLine("🔍 Running track detection debug...");
                            await monitor.DebugTrackDetectionAsync();
                            break;
                        case "sync":
                            Console.WriteLine("🔄 Force syncing track detection...");
                            await monitor.ForceSyncTrackDetectionAsync();
                            break;
                        case "help":
                            ShowHelp();
                            break;
                        case "quit":
                        case "exit":
                            Console.WriteLine("👋 Shutting down...");
                            break;
                        case "":
                            break;
                        default:
                            Console.WriteLine("❓ Unknown command. Available commands:");
                            Console.WriteLine("  'quit' or 'exit' - Stop the application");
                            Console.WriteLine("  'status' - Show current status");
                            Console.WriteLine("  'test' - Generate a test comment");
                            Console.WriteLine("  'debug' - Debug track detection");
                            Console.WriteLine("  'sync' - Force sync track detection");
                            Console.WriteLine("  'help' - Show startup help");
                            break;
                    }
                }
                while (input != "quit" && input != "exit");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.WriteLine("Make sure you're running this on a Linux system with Bluetooth support.");
            }
            finally
            {
                monitor.StopMonitoring();
            }
            
            Console.WriteLine("👋 Goodbye! Thanks for letting me judge your music taste! 🎵");
        }
    }
}

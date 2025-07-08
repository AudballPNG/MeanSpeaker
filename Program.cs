namespace BluetoothSpeaker
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Check for speech configuration
            bool enableSpeech = !args.Contains("--no-speech");
            string ttsVoice = "en+f3"; // Default female voice
            
            // Parse voice parameter if provided
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--voice")
                {
                    ttsVoice = args[i + 1];
                    break;
                }
            }
            
            Console.WriteLine("🎵 Simple Snarky Bluetooth Speaker Starting Up...");
            Console.WriteLine($"Speech: {(enableSpeech ? "Enabled" : "Disabled")}");
            if (enableSpeech)
            {
                Console.WriteLine($"Voice: {ttsVoice}");
            }
            
            // Get OpenAI API key from environment or prompt user
            string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            
            if (string.IsNullOrEmpty(apiKey))
            {
                Console.WriteLine("⚠️ OpenAI API key not found in environment.");
                Console.WriteLine("You can either:");
                Console.WriteLine("1. Set it as environment variable: export OPENAI_API_KEY=\"your-key\"");
                Console.WriteLine("2. Enter it now (it won't be saved):");
                Console.Write("Enter your OpenAI API key (or press Enter to skip AI features): ");
                apiKey = Console.ReadLine()?.Trim();
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("⚠️ No API key provided. AI commentary will be disabled.");
                    apiKey = "dummy-key"; // Use dummy key to continue without AI features
                }
            }
            
            using var monitor = new MusicMonitor(apiKey, enableSpeech, ttsVoice);
            
            try
            {
                await monitor.InitializeAsync();
                await monitor.StartMonitoringAsync();
                
                Console.WriteLine("\n🎵 Simple Bluetooth Speaker is running!");
                Console.WriteLine("Commands:");
                Console.WriteLine("  'quit' or 'exit' - Stop the application");
                Console.WriteLine("  'status' - Show current status");
                Console.WriteLine("  'test' - Generate a test comment");
                Console.WriteLine("  'debug' - Debug track detection");
                Console.WriteLine("  'sync' - Force sync track detection");
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

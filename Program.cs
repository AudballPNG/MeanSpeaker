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
            
            Console.WriteLine("🎵 Snarky Bluetooth Speaker Starting Up...");
            Console.WriteLine($"Speech: {(enableSpeech ? "Enabled" : "Disabled")}");
            if (enableSpeech)
            {
                Console.WriteLine($"Voice: {ttsVoice}");
            }
            
            // Get OpenAI API key from environment or prompt user
            string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            
            if (string.IsNullOrEmpty(apiKey))
            {
                Console.Write("Enter your OpenAI API key: ");
                apiKey = Console.ReadLine()?.Trim();
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("OpenAI API key is required. Exiting...");
                    return;
                }
            }
            
            using var monitor = new MusicMonitor(apiKey, enableSpeech, ttsVoice);
            
            try
            {
                await monitor.InitializeAsync();
                await monitor.StartMonitoringAsync();
                
                Console.WriteLine("\nBluetooth Speaker is running!");
                Console.WriteLine("Commands:");
                Console.WriteLine("  'quit' or 'exit' - Stop the application");
                Console.WriteLine("  'status' - Show current status");
                Console.WriteLine("  'speech on/off' - Toggle speech");
                Console.WriteLine("\nConnect your phone and start playing music to hear my commentary...\n");
                
                string? input;
                do
                {
                    input = Console.ReadLine()?.Trim().ToLower();
                    
                    switch (input)
                    {
                        case "status":
                            Console.WriteLine("Bluetooth Speaker is running and monitoring for music...");
                            break;
                        case "quit":
                        case "exit":
                            Console.WriteLine("Shutting down...");
                            break;
                        case "":
                            break;
                        default:
                            Console.WriteLine("Unknown command. Type 'quit' to exit or 'status' for status.");
                            break;
                    }
                }
                while (input != "quit" && input != "exit");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine("Make sure you're running this on a Linux system with Bluetooth support.");
            }
            finally
            {
                monitor.StopMonitoring();
            }
            
            Console.WriteLine("Goodbye! Thanks for letting me judge your music taste! 🎵");
        }
    }
}

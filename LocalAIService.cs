using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BluetoothSpeaker
{
    public class LocalAIService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _ollamaEndpoint;
        private readonly string _modelName;
        private readonly Random _random;
        private bool _disposed = false;

        // Fallback responses when Ollama is unavailable
        private readonly string[] _fallbackResponses = {
            "Oh great, another song. How original.",
            "This music choice is... interesting. And by interesting, I mean questionable.",
            "Are you trying to torture me with this selection?",
            "I've heard worse, but I'm not sure when.",
            "Your taste in music is truly unique. Unfortunately.",
            "Playing this again? Really? We're doing this?",
            "I suppose someone has to appreciate this music. It won't be me.",
            "This song makes me question my existence as a speaker.",
            "At least it's not as bad as the last one. Wait, yes it is.",
            "I'm starting to think my volume control is my only defense."
        };

        public LocalAIService(string ollamaEndpoint = "http://localhost:11434", string modelName = "phi3:mini")
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30); // Reasonable timeout for local AI
            _ollamaEndpoint = ollamaEndpoint;
            _modelName = modelName;
            _random = new Random();
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_ollamaEndpoint}/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> EnsureModelIsLoadedAsync()
        {
            try
            {
                Console.WriteLine($"🤖 Checking if {_modelName} is available...");
                
                // Check if model exists
                var tagsResponse = await _httpClient.GetAsync($"{_ollamaEndpoint}/api/tags");
                if (!tagsResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine("❌ Ollama server not responding");
                    return false;
                }

                var tagsJson = await tagsResponse.Content.ReadAsStringAsync();
                var tagsDoc = JsonDocument.Parse(tagsJson);
                
                bool modelExists = false;
                if (tagsDoc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var model in models.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out var name) && 
                            name.GetString()?.StartsWith(_modelName.Split(':')[0]) == true)
                        {
                            modelExists = true;
                            break;
                        }
                    }
                }

                if (!modelExists)
                {
                    Console.WriteLine($"📥 Model {_modelName} not found. Pulling from Ollama library...");
                    Console.WriteLine("⏳ This may take a few minutes for the first download...");
                    
                    // Pull the model
                    var pullRequest = new
                    {
                        name = _modelName,
                        stream = false
                    };

                    var pullJson = JsonSerializer.Serialize(pullRequest);
                    var pullContent = new StringContent(pullJson, Encoding.UTF8, "application/json");
                    
                    var pullResponse = await _httpClient.PostAsync($"{_ollamaEndpoint}/api/pull", pullContent);
                    if (!pullResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Failed to pull model {_modelName}");
                        return false;
                    }
                    
                    Console.WriteLine($"✅ Model {_modelName} downloaded successfully");
                }
                else
                {
                    Console.WriteLine($"✅ Model {_modelName} is available");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error checking/loading model: {ex.Message}");
                return false;
            }
        }

        public async Task<string> GenerateCommentAsync(string prompt)
        {
            try
            {
                // Create the chat request
                var request = new
                {
                    model = _modelName,
                    messages = new[]
                    {
                        new
                        {
                            role = "system",
                            content = "You are a snarky, sarcastic Bluetooth speaker with attitude. You judge people's music taste with witty, brief comments (1-2 sentences max). Be clever and mean but not offensive. Think of yourself as a grumpy music critic trapped in a speaker."
                        },
                        new
                        {
                            role = "user",
                            content = prompt
                        }
                    },
                    stream = false,
                    options = new
                    {
                        temperature = 0.8,
                        top_p = 0.9,
                        max_tokens = 100 // Keep responses short and snappy
                    }
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_ollamaEndpoint}/api/chat", content);
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"⚠️ Ollama request failed: {response.StatusCode}");
                    return GetFallbackResponse();
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var responseDoc = JsonDocument.Parse(responseJson);

                if (responseDoc.RootElement.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var responseContent))
                {
                    var aiResponse = responseContent.GetString()?.Trim();
                    
                    if (!string.IsNullOrEmpty(aiResponse))
                    {
                        // Clean up the response - remove quotes if AI wrapped the response
                        aiResponse = aiResponse.Trim('"', '\'');
                        
                        // Ensure it's not too long
                        if (aiResponse.Length > 200)
                        {
                            var sentences = aiResponse.Split('.', StringSplitOptions.RemoveEmptyEntries);
                            aiResponse = sentences[0].Trim() + ".";
                        }
                        
                        Console.WriteLine($"🤖 Local AI: {aiResponse}");
                        return aiResponse;
                    }
                }

                Console.WriteLine("⚠️ Empty response from Ollama");
                return GetFallbackResponse();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Local AI error: {ex.Message}");
                return GetFallbackResponse();
            }
        }

        private string GetFallbackResponse()
        {
            var response = _fallbackResponses[_random.Next(_fallbackResponses.Length)];
            Console.WriteLine($"💬 Fallback: {response}");
            return response;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}

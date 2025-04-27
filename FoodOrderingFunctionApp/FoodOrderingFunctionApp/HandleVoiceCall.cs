using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;

namespace VoicePizzaBot
{
    public class HandleVoiceCall
    {
        private readonly ILogger _logger;
        private readonly CallAutomationClient _callAutomationClient;
        private readonly HttpClient _httpClient;

        // Track silence and language per callConnectionId
        private static Dictionary<string, int> SilenceCounter = new();
        private static Dictionary<string, string> CallLanguageMap = new(); // Language map

        public HandleVoiceCall(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<HandleVoiceCall>();
            _callAutomationClient = new CallAutomationClient(Environment.GetEnvironmentVariable("ACS_CONNECTION_STRING"));
            _httpClient = new HttpClient();
        }

        [Function("HandleVoiceCall")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var callEvent = JsonDocument.Parse(requestBody).RootElement;

            string eventType = callEvent.GetProperty("eventType").GetString() ?? string.Empty;
            _logger.LogInformation($"Received eventType: {eventType}");

            switch (eventType)
            {
                case "Microsoft.Communication.IncomingCall":
                    await HandleIncomingCall(callEvent);
                    break;

                case "Microsoft.Communication.RecognizeCompleted":
                    await HandleRecognizeCompleted(callEvent);
                    break;

                case "Microsoft.Communication.PlayCompleted":
                    await HandlePlayCompleted(callEvent);
                    break;

                case "Microsoft.Communication.CallDisconnected":
                    await HandleCallDisconnected(callEvent);
                    break;

                default:
                    _logger.LogInformation($"Unhandled eventType: {eventType}");
                    break;
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            return response;
        }

        private async Task HandleIncomingCall(JsonElement callEvent)
        {
            string incomingCallContext = callEvent.GetProperty("incomingCallContext").GetString() ?? string.Empty;
            var callbackUri = Environment.GetEnvironmentVariable("ACS_CALLBACK_URL");

            var acceptResult = await _callAutomationClient.AnswerCallAsync(
                incomingCallContext,
                new Uri(callbackUri)
            );

            _logger.LogInformation("Call accepted successfully.");

            string callConnectionId = acceptResult.Value.CallConnectionProperties.CallConnectionId;
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);

            // Play welcome audio
            var playSource = new FileSource(new Uri("https://yourstorageaccount.blob.core.windows.net/audiofiles/welcome.wav"));
            var playOptions = new PlayToAllOptions(playSource)
            {
                Loop = false
            };

            await callConnection.GetCallMedia().PlayToAllAsync(playOptions);
            _logger.LogInformation("Played welcome audio.");

            // Start recognizing speech
            await StartSpeechRecognition(callConnection, "en-US"); // Start with English
        }

        private async Task HandleRecognizeCompleted(JsonElement callEvent)
        {
            string callConnectionId = callEvent.GetProperty("callConnectionId").GetString() ?? string.Empty;
            string recognizedSpeech = callEvent.GetProperty("recognitionResult").GetProperty("speech").GetString() ?? string.Empty;

            _logger.LogInformation($"User said: {recognizedSpeech}");

            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);

            if (string.IsNullOrEmpty(recognizedSpeech))
            {
                _logger.LogWarning("No speech recognized.");

                if (!SilenceCounter.ContainsKey(callConnectionId))
                    SilenceCounter[callConnectionId] = 0;

                SilenceCounter[callConnectionId]++;

                if (SilenceCounter[callConnectionId] == 1)
                {
                    await PlayTextToUser(callConnection, "Sorry, I didn't hear you. Can you please repeat that?", "en-US");
                }
                else
                {
                    await PlayTextToUser(callConnection, "It seems we are having trouble hearing you. Ending the call now. Thank you!", "en-US");
                    await Task.Delay(3000);
                    await callConnection.HangUpAsync(true);
                }

                return;
            }

            if (!CallLanguageMap.ContainsKey(callConnectionId))
            {
                // Detect language first time
                string detectedLanguage = await DetectLanguage(recognizedSpeech);
                CallLanguageMap[callConnectionId] = detectedLanguage;
                _logger.LogInformation($"Detected Language: {detectedLanguage}");
            }

            string languageCode = CallLanguageMap[callConnectionId];

            // Reset silence counter if user speaks
            if (SilenceCounter.ContainsKey(callConnectionId))
                SilenceCounter[callConnectionId] = 0;

            // Normal flow: Generate AI response and play
            var audioUrl = await GenerateSpeechAndUploadToBlob(recognizedSpeech, callConnectionId, languageCode);

            var playSource = new FileSource(new Uri(audioUrl));
            var playOptions = new PlayToAllOptions(playSource)
            {
                Loop = false
            };

            await callConnection.GetCallMedia().PlayToAllAsync(playOptions);

            _logger.LogInformation("Played AI speech audio back to user.");
        }

        private async Task HandlePlayCompleted(JsonElement callEvent)
        {
            string callConnectionId = callEvent.GetProperty("callConnectionId").GetString() ?? string.Empty;
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);

            string languageCode = CallLanguageMap.ContainsKey(callConnectionId) ? CallLanguageMap[callConnectionId] : "en-US";

            await StartSpeechRecognition(callConnection, languageCode);

            _logger.LogInformation("Started listening again after PlayCompleted.");
        }

        private async Task HandleCallDisconnected(JsonElement callEvent)
        {
            string callConnectionId = callEvent.GetProperty("callConnectionId").GetString() ?? string.Empty;

            var blobConnectionString = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING");
            var blobContainerName = Environment.GetEnvironmentVariable("BLOB_CONTAINER_NAME");

            var blobServiceClient = new BlobServiceClient(blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(blobContainerName);
            var blobClient = containerClient.GetBlobClient($"session-{callConnectionId}.wav");

            try
            {
                await blobClient.DeleteIfExistsAsync();
                _logger.LogInformation($"Deleted session audio: session-{callConnectionId}.wav");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to delete session audio: {ex.Message}");
            }
        }

        private async Task<string> GenerateSpeechAndUploadToBlob(string text, string sessionId, string languageCode)
        {
            var subscriptionKey = Environment.GetEnvironmentVariable("AZURE_TTS_KEY");
            var region = Environment.GetEnvironmentVariable("AZURE_TTS_REGION");

            string voice = languageCode switch
            {
                "fr" => "fr-FR-DeniseNeural",
                "hi" => "hi-IN-MadhurNeural",
                _ => "en-US-AriaNeural"
            };

            var ttsEndpoint = Environment.GetEnvironmentVariable("AZURE_TTS_ENDPOINT");
            var requestBody = @$"
                <speak version='1.0' xml:lang='{languageCode}'>
                    <voice name='{voice}'>{text}</voice>
                </speak>";

            using var ttsRequest = new HttpRequestMessage(HttpMethod.Post, ttsEndpoint);
            ttsRequest.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
            ttsRequest.Headers.Add("X-Microsoft-OutputFormat", "riff-16khz-16bit-mono-pcm");
            ttsRequest.Content = new StringContent(requestBody);
            ttsRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ssml+xml");

            var ttsResponse = await _httpClient.SendAsync(ttsRequest);
            ttsResponse.EnsureSuccessStatusCode();

            var audioStream = await ttsResponse.Content.ReadAsStreamAsync();

            var blobConnectionString = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING");
            var blobContainerName = Environment.GetEnvironmentVariable("BLOB_CONTAINER_NAME");

            var blobServiceClient = new BlobServiceClient(blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(blobContainerName);
            var blobClient = containerClient.GetBlobClient($"session-{sessionId}.wav");

            await blobClient.UploadAsync(audioStream, overwrite: true);

            return blobClient.Uri.ToString();
        }

        private async Task PlayTextToUser(CallConnection callConnection, string text, string languageCode)
        {
            var audioUrl = await GenerateSpeechAndUploadToBlob(text, callConnection.CallConnectionId, languageCode);

            var playSource = new FileSource(new Uri(audioUrl));
            var playOptions = new PlayToAllOptions(playSource)
            {
                Loop = false
            };

            await callConnection.GetCallMedia().PlayToAllAsync(playOptions);
        }

        private async Task StartSpeechRecognition(CallConnection callConnection, string languageCode)
        {
            var recognizeOptions = new CallMediaRecognizeSpeechOptions(
                new CommunicationUserIdentifier(Environment.GetEnvironmentVariable("ACS_USER_ID"))
            )
            {
                InterruptPrompt = true,
                SpeechLanguage = languageCode,
                InitialSilenceTimeout = TimeSpan.FromSeconds(5)
            };

            await callConnection.GetCallMedia().StartRecognizingAsync(recognizeOptions);
        }

        private async Task<string> DetectLanguage(string text)
        {
            var subscriptionKey = Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_KEY");
            var region = Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_REGION");

            var url = "https://api.cognitive.microsofttranslator.com/detect?api-version=3.0";

            var requestBody = JsonSerializer.Serialize(new[] { new { Text = text } });
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
            request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
            request.Headers.Add("Ocp-Apim-Subscription-Region", region);

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(responseBody);

            return jsonDoc.RootElement[0].GetProperty("language").GetString() ?? "en";
        }
    }
}

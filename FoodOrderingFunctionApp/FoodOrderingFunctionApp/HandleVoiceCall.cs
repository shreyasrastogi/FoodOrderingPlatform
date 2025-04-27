using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using Azure.Messaging.EventGrid.SystemEvents;

namespace VoicePizzaBot
{
    public class HandleVoiceCall
    {
        private readonly ILogger _logger;
        private readonly CallAutomationClient _callAutomationClient;
        private readonly HttpClient _httpClient;

        // Track silence, language, and AI session per callConnectionId
        private static Dictionary<string, int> SilenceCounter = new();
        private static Dictionary<string, string> CallLanguageMap = new();
        private static Dictionary<string, string> CallSessionMap = new(); // NEW: Track agent sessions

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
            var events = EventGridEvent.ParseMany(BinaryData.FromString(requestBody));

            foreach (var eventGridEvent in events)
            {
                if (eventGridEvent.EventType == "Microsoft.EventGrid.SubscriptionValidationEvent")
                {
                    var data = eventGridEvent.Data.ToObjectFromJson<SubscriptionValidationEventData>();
                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteAsJsonAsync(new { validationResponse = data.ValidationCode });
                    return response;
                }
                else if (eventGridEvent.EventType == "Microsoft.Communication.IncomingCall")
                {
                    var callEvent = JsonDocument.Parse(eventGridEvent.Data.ToStream()).RootElement;
                    await HandleIncomingCall(callEvent);
                }
                else if (eventGridEvent.EventType == "Microsoft.Communication.RecognizeCompleted")
                {
                    var callEvent = JsonDocument.Parse(eventGridEvent.Data.ToStream()).RootElement;
                    await HandleRecognizeCompleted(callEvent);
                }
                else if (eventGridEvent.EventType == "Microsoft.Communication.PlayCompleted")
                {
                    var callEvent = JsonDocument.Parse(eventGridEvent.Data.ToStream()).RootElement;
                    await HandlePlayCompleted(callEvent);
                }
                else if (eventGridEvent.EventType == "Microsoft.Communication.CallDisconnected")
                {
                    var callEvent = JsonDocument.Parse(eventGridEvent.Data.ToStream()).RootElement;
                    await HandleCallDisconnected(callEvent);
                }
            }

            var okResponse = req.CreateResponse(HttpStatusCode.OK);
            return okResponse;
        }

        private async Task HandleIncomingCall(JsonElement callEvent)
        {
            string incomingCallContext = callEvent.GetProperty("incomingCallContext").GetString();
            var callbackUri = Environment.GetEnvironmentVariable("ACS_CALLBACK_URL");

            var acceptResult = await _callAutomationClient.AnswerCallAsync(
                incomingCallContext,
                new Uri(callbackUri)
            );

            string callConnectionId = acceptResult.Value.CallConnectionProperties.CallConnectionId;
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);

            // Create new AI Agent session
            string sessionId = await CreateAgentSession();
            CallSessionMap[callConnectionId] = sessionId;

            _logger.LogInformation($"Call accepted. Created new agent session: {sessionId}");

            // Play welcome audio or directly start speech recognition
            await StartSpeechRecognition(callConnection, "en-US");
        }

        private async Task HandleRecognizeCompleted(JsonElement callEvent)
        {
            string callConnectionId = callEvent.GetProperty("callConnectionId").GetString();
            string recognizedSpeech = callEvent.GetProperty("recognitionResult").GetProperty("speech").GetString();

            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);

            if (string.IsNullOrEmpty(recognizedSpeech))
            {
                if (!SilenceCounter.ContainsKey(callConnectionId))
                    SilenceCounter[callConnectionId] = 0;

                SilenceCounter[callConnectionId]++;
                if (SilenceCounter[callConnectionId] == 1)
                {
                    await PlayTextToUser(callConnection, "Sorry, I didn't hear you. Can you repeat?", "en-US");
                }
                else
                {
                    await PlayTextToUser(callConnection, "Ending the call due to no response. Thank you!", "en-US");
                    await Task.Delay(3000);
                    await callConnection.HangUpAsync(true);
                }
                return;
            }

            if (!CallLanguageMap.ContainsKey(callConnectionId))
            {
                string detectedLanguage = await DetectLanguage(recognizedSpeech);
                CallLanguageMap[callConnectionId] = detectedLanguage;
            }

            string languageCode = CallLanguageMap[callConnectionId];
            if (SilenceCounter.ContainsKey(callConnectionId)) SilenceCounter[callConnectionId] = 0;

            // Send to AI Agent
            string agentResponse = await GetAgentResponseFromAI(callConnectionId, recognizedSpeech);

            var audioUrl = await GenerateSpeechAndUploadToBlob(agentResponse, callConnectionId, languageCode);
            await callConnection.GetCallMedia().PlayToAllAsync(new PlayToAllOptions(new FileSource(new Uri(audioUrl))));
        }

        private async Task HandlePlayCompleted(JsonElement callEvent)
        {
            string callConnectionId = callEvent.GetProperty("callConnectionId").GetString();
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);

            string languageCode = CallLanguageMap.ContainsKey(callConnectionId) ? CallLanguageMap[callConnectionId] : "en-US";

            await StartSpeechRecognition(callConnection, languageCode);
        }

        private async Task HandleCallDisconnected(JsonElement callEvent)
        {
            string callConnectionId = callEvent.GetProperty("callConnectionId").GetString();

            // Cleanup Agent session
            if (CallSessionMap.ContainsKey(callConnectionId))
            {
                string sessionId = CallSessionMap[callConnectionId];
                await DeleteAgentSession(sessionId);
                CallSessionMap.Remove(callConnectionId);
            }

            var blobConnectionString = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING");
            var blobContainerName = Environment.GetEnvironmentVariable("BLOB_CONTAINER_NAME");

            var blobServiceClient = new BlobServiceClient(blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(blobContainerName);
            var blobClient = containerClient.GetBlobClient($"session-{callConnectionId}.wav");

            await blobClient.DeleteIfExistsAsync();
        }

        private async Task<string> CreateAgentSession()
        {
            var agentEndpoint = Environment.GetEnvironmentVariable("AI_AGENT_ENDPOINT");
            var response = await _httpClient.PostAsync($"{agentEndpoint}/sessions", null);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(content);
            return jsonDoc.RootElement.GetProperty("sessionId").GetString();
        }

        private async Task<string> GetAgentResponseFromAI(string callConnectionId, string userInput)
        {
            var agentEndpoint = Environment.GetEnvironmentVariable("AI_AGENT_ENDPOINT");
            string sessionId = CallSessionMap.ContainsKey(callConnectionId) ? CallSessionMap[callConnectionId] : "";

            var payload = new { input = userInput };
            var response = await _httpClient.PostAsJsonAsync($"{agentEndpoint}/sessions/{sessionId}/messages", payload);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(content);
            return jsonDoc.RootElement.GetProperty("response").GetString() ?? "Sorry, I did not understand that.";
        }

        private async Task DeleteAgentSession(string sessionId)
        {
            var agentEndpoint = Environment.GetEnvironmentVariable("AI_AGENT_ENDPOINT");
            await _httpClient.DeleteAsync($"{agentEndpoint}/sessions/{sessionId}");
        }

        private async Task PlayTextToUser(CallConnection callConnection, string text, string languageCode)
        {
            var audioUrl = await GenerateSpeechAndUploadToBlob(text, callConnection.CallConnectionId, languageCode);
            await callConnection.GetCallMedia().PlayToAllAsync(new PlayToAllOptions(new FileSource(new Uri(audioUrl))));
        }

        private async Task StartSpeechRecognition(CallConnection callConnection, string languageCode)
        {
            var recognizeOptions = new CallMediaRecognizeSpeechOptions(
                new CommunicationUserIdentifier(Environment.GetEnvironmentVariable("ACS_USER_ID")))
            {
                InterruptPrompt = true,
                SpeechLanguage = languageCode,
                InitialSilenceTimeout = TimeSpan.FromSeconds(5)
            };
            await callConnection.GetCallMedia().StartRecognizingAsync(recognizeOptions);
        }

        private async Task<string> GenerateSpeechAndUploadToBlob(string text, string sessionId, string languageCode)
        {
            var subscriptionKey = Environment.GetEnvironmentVariable("AZURE_TTS_KEY");
            var ttsEndpoint = Environment.GetEnvironmentVariable("AZURE_TTS_ENDPOINT");

            string voice = languageCode switch
            {
                "fr" => "fr-FR-DeniseNeural",
                "hi" => "hi-IN-MadhurNeural",
                _ => "en-US-AriaNeural"
            };

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

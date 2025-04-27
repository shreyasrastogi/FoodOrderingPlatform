using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Azure.Communication.CallAutomation;
using System;
using Azure.Communication;

namespace VoicePizzaBot
{
    public class HandleVoiceCall
    {
        private readonly ILogger _logger;
        private readonly CallAutomationClient _callAutomationClient;
        private readonly HttpClient _httpClient;

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
            _logger.LogInformation($"Received payload: {requestBody}"); // Log the raw payload for debugging

            var events = JsonDocument.Parse(requestBody).RootElement.EnumerateArray();

            foreach (var eventObj in events)
            {
                if (!eventObj.TryGetProperty("type", out var eventTypeElement) || eventTypeElement.GetString() is null)
                {
                    _logger.LogWarning($"Event type is missing or null. Skipping event. Raw event: {eventObj}");
                    continue;
                }

                string eventType = eventTypeElement.GetString();
                _logger.LogInformation($"Processing eventType: {eventType}");

                if (eventType == "Microsoft.Communication.IncomingCall")
                {
                    if (eventObj.TryGetProperty("data", out var eventData))
                    {
                        await HandleIncomingCall(eventData);
                    }
                    else
                    {
                        _logger.LogWarning("Incoming call event data is missing. Skipping event.");
                    }
                }
                else if (eventType == "Microsoft.Communication.RecognizeCompleted")
                {
                    if (eventObj.TryGetProperty("data", out var eventData))
                    {
                        await HandleRecognizeCompleted(eventData);
                    }
                    else
                    {
                        _logger.LogWarning("Recognize completed event data is missing. Skipping event.");
                    }
                }
                else if (eventType == "Microsoft.Communication.CallConnected")
                {
                    _logger.LogInformation("Call connected event received. No action needed.");
                }
                else if (eventType == "Microsoft.Communication.ParticipantsUpdated")
                {
                    _logger.LogInformation("Participants updated event received. No action needed.");
                }
                else if (eventType == "Microsoft.Communication.AnswerFailed")
                {
                    _logger.LogError("Answer failed event received. Check ACS configuration or logs for details.");
                }
                else
                {
                    _logger.LogWarning($"Unhandled eventType: {eventType}. Skipping event.");
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            return response;
        }

        private async Task HandleIncomingCall(JsonElement callEvent)
        {
            if (!callEvent.TryGetProperty("incomingCallContext", out var incomingCallContextElement) || incomingCallContextElement.GetString() is null)
            {
                _logger.LogError("Incoming call context is missing or null.");
                return;
            }

            string incomingCallContext = incomingCallContextElement.GetString();
            var callbackUri = Environment.GetEnvironmentVariable("ACS_CALLBACK_URL");

            if (string.IsNullOrEmpty(callbackUri))
            {
                _logger.LogError("Callback URI is not configured.");
                return;
            }

            var acceptResult = await _callAutomationClient.AnswerCallAsync(
                incomingCallContext,
                new Uri(callbackUri)
            );

            string callConnectionId = acceptResult.Value.CallConnectionProperties.CallConnectionId;
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);

            _logger.LogInformation("Call accepted. Starting speech recognition...");
            await StartSpeechRecognition(callConnection, "en-US");
        }

        private async Task HandleRecognizeCompleted(JsonElement callEvent)
        {
            if (!callEvent.TryGetProperty("callConnectionId", out var callConnectionIdElement) || callConnectionIdElement.GetString() is null)
            {
                _logger.LogError("Call connection ID is missing or null.");
                return;
            }

            string callConnectionId = callConnectionIdElement.GetString();

            if (!callEvent.TryGetProperty("recognitionResult", out var recognitionResult) ||
                !recognitionResult.TryGetProperty("speech", out var speechElement) ||
                speechElement.GetString() is null)
            {
                _logger.LogWarning("Recognition result or speech is missing or null.");
                return;
            }

            string recognizedSpeech = speechElement.GetString();
            _logger.LogInformation($"User said: {recognizedSpeech}");

            // Forward user input to Azure AI agent
            string agentResponse = await GetAgentResponseFromAI(recognizedSpeech);

            // Play the AI agent's response back to the caller
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);
            await PlayTextToUser(callConnection, agentResponse, "en-US");
        }

        private async Task<string> GetAgentResponseFromAI(string userInput)
        {
            var agentEndpoint = Environment.GetEnvironmentVariable("AI_AGENT_ENDPOINT");
            var payload = new { input = userInput };

            var response = await _httpClient.PostAsJsonAsync($"{agentEndpoint}/messages", payload);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(content);
            return jsonDoc.RootElement.GetProperty("response").GetString() ?? "Sorry, I did not understand that.";
        }

        private async Task PlayTextToUser(CallConnection callConnection, string text, string languageCode)
        {
            var playSource = new TextSource(text);
            var playOptions = new PlayToAllOptions(playSource)
            {
                Loop = false
            };

            await callConnection.GetCallMedia().PlayToAllAsync(playOptions);
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
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Azure;
using Azure.Identity;
using Azure.AI.Projects;
using Microsoft.OpenApi.Models;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;

namespace FoodOrderingFunctionApp
{
    public static class TalkToAgentFunction
    {
        [FunctionName("TalkToAgent")]
        [OpenApiOperation(operationId: "TalkToAgent", tags: new[] { "Agent" }, Summary = "Talk to AI Agent", Description = "Sends a message to the AI agent and retrieves a response.")]
        [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(TalkToAgentRequest), Required = true, Description = "User input for the AI agent")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(TalkToAgentResponse), Description = "Response from the AI agent")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid input")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Received request to talk to AI Agent.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonConvert.DeserializeObject<TalkToAgentRequest>(requestBody);

            if (data == null || string.IsNullOrEmpty(data.Input))
            {
                return new BadRequestObjectResult(new { message = "Please pass a valid 'input' string in the request body." });
            }

            var connectionString = "eastus.api.azureml.ms;864dfcb3-c1b9-493d-8751-2774190cb56a;fooddeliveryplatform;foodorderingplatform";
            var agentId = "asst_D9RoP8fWL3vk6quZXJ9eGKjO";
            var threadId = "thread_U3UMd55BWDxyy5ejWNIu6aVd";

            AgentsClient client = new AgentsClient(connectionString, new DefaultAzureCredential());

            try
            {
                // Retrieve agent and thread
                Response<Agent> agentResponse = await client.GetAgentAsync(agentId);
                Response<AgentThread> threadResponse = await client.GetThreadAsync(threadId);

                // Create a new message in the thread
                Response<ThreadMessage> messageResponse = await client.CreateMessageAsync(
                    threadId,
                    MessageRole.User,
                    data.Input);

                // Start a run for the agent
                Response<ThreadRun> runResponse = await client.CreateRunAsync(threadId, agentId);

                // Poll until the run is completed
                do
                {
                    await Task.Delay(500);
                    runResponse = await client.GetRunAsync(threadId, runResponse.Value.Id);
                }
                while (runResponse.Value.Status == RunStatus.Queued || runResponse.Value.Status == RunStatus.InProgress);

                // Get updated messages
                Response<PageableList<ThreadMessage>> messagesResponse = await client.GetMessagesAsync(threadId);
                IReadOnlyList<ThreadMessage> messages = messagesResponse.Value.Data;

                // Find the latest assistant message
                string agentReply = null;
                foreach (var threadMessage in messages)
                {
                    if (threadMessage.Role == MessageRole.Agent)
                    {
                        foreach (var contentItem in threadMessage.ContentItems)
                        {
                            if (contentItem is MessageTextContent textItem)
                            {
                                agentReply = textItem.Text;
                                break;
                            }
                        }
                    }
                }

                if (agentReply != null)
                {
                    return new OkObjectResult(new TalkToAgentResponse { Response = agentReply });
                }
                else
                {
                    return new OkObjectResult(new TalkToAgentResponse { Response = "No reply received from agent." });
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Error communicating with Agent: {ex.Message}");
                return new StatusCodeResult(500);
            }
        }
    }

    public class TalkToAgentRequest
    {
        public string Input { get; set; }
    }

    public class TalkToAgentResponse
    {
        public string Response { get; set; }
    }
}

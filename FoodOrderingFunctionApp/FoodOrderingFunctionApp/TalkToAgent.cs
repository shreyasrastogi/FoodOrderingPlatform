using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Azure.AI.Projects;
using Azure;
using Azure.Identity;

namespace AgentFunction
{
    public static class AgentServiceFunction
    {
        [FunctionName("TalkToAgent")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Received request to talk to AI Agent.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string userInput = data?.input;
            if (string.IsNullOrEmpty(userInput))
            {
                return new BadRequestObjectResult("Please pass an 'input' string in the request body.");
            }

            var connectionString = "eastus.api.azureml.ms;864dfcb3-c1b9-493d-8751-2774190cb56a;fooddeliveryplatform;foodorderingplatform";
            var agentId = "asst_D9RoP8fWL3vk6quZXJ9eGKjO";
            var threadId = "thread_U3UMd55BWDxyy5ejWNIu6aVd";

            AgentsClient client = new AgentsClient(connectionString, new DefaultAzureCredential());

            try
            {
                // Create a new message in the thread
                Response<ThreadMessage> messageResponse = await client.CreateMessageAsync(
                    threadId,
                    MessageRole.User,
                    userInput);

                // Start a run for the agent
                Response<ThreadRun> runResponse = await client.CreateRunAsync(threadId, agentId);
                ThreadRun run = runResponse.Value;

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
                    return new OkObjectResult(new { response = agentReply });
                }
                else
                {
                    return new OkObjectResult(new { response = "No reply received from agent." });
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Error communicating with Agent: {ex.Message}");
                return new StatusCodeResult(500);
            }
        }
    }
}

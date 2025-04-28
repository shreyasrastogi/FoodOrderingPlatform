using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Azure;
using Azure.Identity;
using Azure.AI.Projects;

namespace FoodOrderingFunctionApp
{
    public static class TalkToAgentFunction
    {
        [Function("TalkToAgent")]
        public static async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
            FunctionContext context)
        {
            var log = context.GetLogger("TalkToAgent");
            log.LogInformation("TalkToAgent function received a request.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonConvert.DeserializeObject<TalkToAgentRequest>(requestBody);

            if (data == null || string.IsNullOrEmpty(data.Input))
            {
                var badRequestResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(new { message = "Please pass a valid 'input' string in the request body." });
                return badRequestResponse;
            }

            var connectionString = "eastus.api.azureml.ms;864dfcb3-c1b9-493d-8751-2774190cb56a;fooddeliveryplatform;foodorderingplatform";
            var agentId = "asst_D9RoP8fWL3vk6quZXJ9eGKjO";
            var threadId = "thread_U3UMd55BWDxyy5ejWNIu6aVd";

            AgentsClient client = new AgentsClient(connectionString, new DefaultAzureCredential());

            try
            {
                Response<Agent> agentResponse = await client.GetAgentAsync(agentId);
                Response<AgentThread> threadResponse = await client.GetThreadAsync(threadId);

                Response<ThreadMessage> messageResponse = await client.CreateMessageAsync(
                    threadId,
                    MessageRole.User,
                    data.Input);

                Response<ThreadRun> runResponse = await client.CreateRunAsync(threadId, agentId);

                do
                {
                    await Task.Delay(500);
                    runResponse = await client.GetRunAsync(threadId, runResponse.Value.Id);
                }
                while (runResponse.Value.Status == RunStatus.Queued || runResponse.Value.Status == RunStatus.InProgress);

                Response<PageableList<ThreadMessage>> messagesResponse = await client.GetMessagesAsync(threadId);
                IReadOnlyList<ThreadMessage> messages = messagesResponse.Value.Data;

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

                var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new TalkToAgentResponse { Response = agentReply ?? "No reply received from agent." });
                return response;
            }
            catch (Exception ex)
            {
                log.LogError($"Error communicating with Agent: {ex.Message}");
                var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { message = "An error occurred while processing your request." });
                return errorResponse;
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
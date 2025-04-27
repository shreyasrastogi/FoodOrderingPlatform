using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;

namespace FoodOrderingFunctionApp
{
    // Define a strongly-typed model for menu items
    public class MenuItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Prices Prices { get; set; }
        public List<string> Toppings { get; set; } // New property for toppings
        public string Category { get; set; } // New property for category
    }

    public class Prices
    {
        public double Small { get; set; }
        public double Medium { get; set; }
        public double Large { get; set; }
    }

    public class GetMenuItems
    {
        private readonly ILogger<GetMenuItems> _logger;
        private readonly CosmosClient _cosmosClient;
        private readonly Container _container;

        public GetMenuItems(ILogger<GetMenuItems> logger, IConfiguration configuration)
        {
            _logger = logger;

            // Read settings from local.settings.json
            var cosmosDbSettings = configuration.GetSection("CosmosDb");
            string connectionString = cosmosDbSettings["ConnectionString"];
            string databaseName = cosmosDbSettings["DatabaseName"];
            string containerName = cosmosDbSettings["ContainerName"];

            // Initialize CosmosClient and Container
            _cosmosClient = new CosmosClient(connectionString);
            _container = _cosmosClient.GetContainer(databaseName, containerName);
        }

        [Function("GetMenuItems")]
        [OpenApiOperation(operationId: "GetMenuItems", tags: new[] { "Menu" }, Summary = "Get menu items", Description = "Fetches all menu items from the database.")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(List<MenuItem>), Description = "List of menu items")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req)
        {
            _logger.LogInformation("Fetching menu items from Cosmos DB.");

            try
            {
                // Query items from the FoodMenuItems container
                var query = new QueryDefinition("SELECT * FROM c");
                var iterator = _container.GetItemQueryIterator<MenuItem>(query);
                var menuItems = new List<MenuItem>();

                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    menuItems.AddRange(response.ToList());
                }

                // Return the menu items as JSON
                return new OkObjectResult(menuItems);
            }
            catch (CosmosException ex)
            {
                _logger.LogError($"Cosmos DB error: {ex.Message}");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"General error: {ex.Message}");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
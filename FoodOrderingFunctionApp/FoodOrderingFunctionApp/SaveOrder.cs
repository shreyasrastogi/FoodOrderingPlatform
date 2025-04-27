using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Cosmos;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi.Models;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;

namespace FoodOrderingFunctionApp
{
    public class SaveOrder
    {
        private readonly ILogger<SaveOrder> _logger;
        private readonly CosmosClient _cosmosClient;
        private readonly Container _container;

        public SaveOrder(ILogger<SaveOrder> logger, IConfiguration configuration)
        {
            _logger = logger;

            // Read Cosmos DB settings from configuration
            var cosmosDbSettings = configuration.GetSection("CosmosDb");
            string connectionString = cosmosDbSettings["ConnectionString"];
            string databaseName = cosmosDbSettings["DatabaseName"];
            string containerName = "FoodOrders"; // Specify the container for orders

            // Initialize CosmosClient and Container
            _cosmosClient = new CosmosClient(connectionString);
            _container = _cosmosClient.GetContainer(databaseName, containerName);
        }

        [Function("SaveOrder")]
        [OpenApiOperation(operationId: "SaveOrder", tags: new[] { "Order" }, Summary = "Save an order", Description = "Saves a new order to the database.")]
        [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(Order), Required = true, Description = "Order details")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(object), Description = "Order saved successfully")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid order format or missing items")]
        [OpenApiResponseWithBody(statusCode: System.Net.HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
        {
            _logger.LogInformation("Saving order to Cosmos DB.");

            try
            {
                // Read the request body
                string requestBody = await req.ReadAsStringAsync();
                var order = JsonSerializer.Deserialize<Order>(requestBody);

                // Validate the order
                if (order == null || order.Items == null || !order.Items.Any())
                {
                    var badRequestResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteAsJsonAsync(new { message = "Invalid order format or missing items." });
                    return badRequestResponse;
                }

                // Calculate the total price
                order.TotalPrice = order.Items.Sum(item => item.Price * item.Quantity);

                // Generate a unique Order ID if not provided
                if (string.IsNullOrEmpty(order.id))
                {
                    order.id = System.Guid.NewGuid().ToString();
                }

                // Save the order to Cosmos DB
                await _container.CreateItemAsync(order, new PartitionKey(order.id));

                // Return success response
                var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { message = "Order saved successfully", orderId = order.id, totalPrice = order.TotalPrice });
                return response;
            }
            catch (CosmosException ex)
            {
                _logger.LogError($"Cosmos DB error: {ex.Message}");
                return req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            }
        }
    }

    // Define the Order model
    public class Order
    {
        public string id { get; set; } // Unique identifier for the order
        public string CustomerName { get; set; } // Customer's name
        public string PhoneNumber { get; set; } // Customer's phone number
        public string Email { get; set; } // Customer's email
        public string Address { get; set; } // Customer's address
        public List<OrderItem> Items { get; set; } // List of items in the order
        public double TotalPrice { get; set; } // Calculated as sum of all item prices * quantity
    }

    // Define the OrderItem model
    public class OrderItem
    {
        public string ItemName { get; set; } // Name of the item
        public List<string> Toppings { get; set; } // List of toppings
        public string Category { get; set; } // Category of the item (e.g., Veg, Non-Veg)
        public string Size { get; set; } // Size of the item (e.g., Small, Medium, Large)
        public double Price { get; set; } // Price of the item
        public int Quantity { get; set; } // Quantity of the item
    }
}

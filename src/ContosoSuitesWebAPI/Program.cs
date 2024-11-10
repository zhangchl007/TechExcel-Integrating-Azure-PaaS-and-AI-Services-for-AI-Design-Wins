using Azure.Identity;
using Microsoft.Azure.Cosmos;
using ContosoSuitesWebAPI.Agents;
using ContosoSuitesWebAPI.Entities;
using ContosoSuitesWebAPI.Plugins;
using ContosoSuitesWebAPI.Services;
using Microsoft.Data.SqlClient;
using Azure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using System.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add configuration to read from user secrets
builder.Configuration.AddUserSecrets<Program>();

// Add configuration to read from environment variables
builder.Configuration.AddEnvironmentVariables();

// Add configuration to read from appsettings.json
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
if (builder.Environment.IsDevelopment())
{
    // Add configuration to read from appsettings.Development.json
    builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
}

// Build the configuration
var config = builder.Configuration;

// Log the environment and configuration sources
Console.WriteLine($"Environment: {builder.Environment.EnvironmentName}");
Console.WriteLine($"CosmosDB Connection String from Configuration: {config["CosmosDB:ConnectionString"]}");

// Retrieve the CosmosDB connection string from the configuration
string cosmosDbConnectionString = config["CosmosDB:ConnectionString"];
if (string.IsNullOrEmpty(cosmosDbConnectionString))
{
    Console.WriteLine("CosmosDB connection string is not configured correctly.");
    throw new ArgumentException("CosmosDB connection string is not configured correctly.");
}

// Add services to the container
builder.Services.AddSingleton<Kernel>((_) =>
{
    IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
    #pragma warning disable SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    kernelBuilder.AddAzureOpenAITextEmbeddingGeneration(
        deploymentName: builder.Configuration["AzureOpenAI:EmbeddingDeploymentName"]!,
        endpoint: builder.Configuration["AzureOpenAI:Endpoint"]!,
        apiKey: builder.Configuration["AzureOpenAI:ApiKey"]!
    );
    #pragma warning restore SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    kernelBuilder.AddAzureOpenAIChatCompletion(
        deploymentName: builder.Configuration["AzureOpenAI:DeploymentName"]!,
        endpoint: builder.Configuration["AzureOpenAI:Endpoint"]!,
        apiKey: builder.Configuration["AzureOpenAI:ApiKey"]!
    );

    kernelBuilder.Plugins.AddFromType<DatabaseService>();
    kernelBuilder.Plugins.AddFromType<MaintenanceRequestPlugin>("MaintenanceCopilot");

    // Create a single instance of the CosmosClient to be shared across the application.
    kernelBuilder.Services.AddSingleton<CosmosClient>((_) =>
    {
        CosmosClient client = new(cosmosDbConnectionString);
        return client;
    });
    return kernelBuilder.Build();
});

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Use dependency injection to inject services into the application.
builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
builder.Services.AddSingleton<IVectorizationService, VectorizationService>();
builder.Services.AddSingleton<MaintenanceCopilot, MaintenanceCopilot>();

// Create a single instance of the CosmosClient to be shared across the application.
builder.Services.AddSingleton<CosmosClient>((_) =>
{
    CosmosClient client = new(cosmosDbConnectionString);
    return client;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

/**** Endpoints ****/
// This endpoint serves as the default landing page for the API.
app.MapGet("/", async () => 
{
    return "Welcome to the Contoso Suites Web API!";
})
    .WithName("Index")
    .WithOpenApi();

// Retrieve the set of hotels from the database.
app.MapGet("/Hotels", async () => 
{
    var hotels = await app.Services.GetRequiredService<IDatabaseService>().GetHotels();
    return hotels;
})
    .WithName("GetHotels")
    .WithOpenApi();

// Retrieve the bookings for a specific hotel.
app.MapGet("/Hotels/{hotelId}/Bookings/", async (int hotelId) => 
{
    var bookings = await app.Services.GetRequiredService<IDatabaseService>().GetBookingsForHotel(hotelId);
    return bookings;
})
    .WithName("GetBookingsForHotel")
    .WithOpenApi();

// Retrieve the bookings for a specific hotel that are after a specified date.
app.MapGet("/Hotels/{hotelId}/Bookings/{min_date}", async (int hotelId, DateTime min_date) => 
{
    var bookings = await app.Services.GetRequiredService<IDatabaseService>().GetBookingsByHotelAndMinimumDate(hotelId, min_date);
    return bookings;
})
    .WithName("GetRecentBookingsForHotel")
    .WithOpenApi();

// This endpoint is used to send a message to the Azure OpenAI endpoint.
app.MapPost("/Chat", async Task<string> (HttpRequest request) =>
{
    var message = await Task.FromResult(request.Form["message"]);
    var kernel = app.Services.GetRequiredService<Kernel>();
    var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
    var executionSettings = new OpenAIPromptExecutionSettings
    {
        ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
    };
    var response = await chatCompletionService.GetChatMessageContentAsync(message.ToString(), executionSettings, kernel);
    return response?.Content!;
})
    .WithName("Chat")
    .WithOpenApi();

// This endpoint is used to vectorize a text string.
// We will use this to generate embeddings for the maintenance request text.
app.MapGet("/Vectorize", async (string text, [FromServices] IVectorizationService vectorizationService) =>
{
    var embeddings = await vectorizationService.GetEmbeddings(text);
    return embeddings;
})
    .WithName("Vectorize")
    .WithOpenApi();

// This endpoint is used to search for maintenance requests based on a vectorized query.
app.MapPost("/VectorSearch", async ([FromBody] float[] queryVector, [FromServices] IVectorizationService vectorizationService, int max_results = 0, double minimum_similarity_score = 0.8) =>
{
    var results = await vectorizationService.ExecuteVectorSearch(queryVector, max_results, minimum_similarity_score);
    return results;
})
    .WithName("VectorSearch")
    .WithOpenApi();

// This endpoint is used to send a message to the Maintenance Copilot.
app.MapPost("/MaintenanceCopilotChat", async ([FromBody]string message, [FromServices] MaintenanceCopilot copilot) =>
{
    var response = await copilot.Chat(message);
    return response;
})
    .WithName("Copilot")
    .WithOpenApi();

app.Run();
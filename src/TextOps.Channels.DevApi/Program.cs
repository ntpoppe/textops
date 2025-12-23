using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TextOps.Channels.DevApi.Execution;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;
using TextOps.Worker.Stub;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        // Use ProblemDetails for validation errors
        options.InvalidModelStateResponseFactory = context =>
        {
            var problemDetails = new ProblemDetails
            {
                Title = "Validation Error",
                Status = StatusCodes.Status400BadRequest,
                Detail = string.Join(" ", context.ModelState
                    .SelectMany(x => x.Value?.Errors ?? Enumerable.Empty<Microsoft.AspNetCore.Mvc.ModelBinding.ModelError>())
                    .Select(x => x.ErrorMessage))
            };
            return new BadRequestObjectResult(problemDetails);
        };
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// Register orchestrator and parser as singletons for in-memory state persistence
builder.Services.AddSingleton<IRunOrchestrator, InMemoryRunOrchestrator>();
builder.Services.AddSingleton<IIntentParser, DeterministicIntentParser>();

// Register execution queue and dispatcher
builder.Services.AddSingleton<InMemoryExecutionQueue>();
builder.Services.AddSingleton<IExecutionDispatcher>(sp => sp.GetRequiredService<InMemoryExecutionQueue>());

// Register worker executor
builder.Services.AddSingleton<IWorkerExecutor>(sp =>
{
    var orchestrator = sp.GetRequiredService<IRunOrchestrator>();
    return new StubWorkerExecutor(orchestrator);
});

// Register execution hosted service
builder.Services.AddHostedService<ExecutionHostedService>();

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseHttpsRedirection();
app.MapControllers();

app.Run();

using Microsoft.Extensions.Hosting;
using OrderFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOrderFlowWorker(builder.Configuration);
using var host = builder.Build();

await host.RunAsync();

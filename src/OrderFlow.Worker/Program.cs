using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);
// Structured console output keeps the correlation scope on every line, so one order can be followed across the API and Worker logs.
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddOrderFlowWorker(builder.Configuration);
using var host = builder.Build();

await host.RunAsync();

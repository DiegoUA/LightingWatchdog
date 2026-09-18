using Microsoft.Extensions.Hosting.WindowsServices;
using NetworkWatchdogService;
using NetworkWatchdogService.Models;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() 
        ? AppContext.BaseDirectory 
        : default
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "NetworkWatchdog";
});

// Bind the configuration from appsettings.json
builder.Services.Configure<WatchdogConfig>(builder.Configuration.GetSection("WatchdogSettings"));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
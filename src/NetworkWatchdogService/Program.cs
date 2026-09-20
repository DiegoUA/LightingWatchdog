using Microsoft.Extensions.Hosting.WindowsServices;
using NetworkWatchdogService;
using NetworkWatchdogService.Models;
using System;
    using System.IO;

    // ---> NEW: Fix Windows Service working directory defaulting to System32
    Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : default
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LightingWatchdogService";
});

// CRITICAL: This must exactly match the key in appsettings.json
builder.Services.Configure<WatchdogConfig>(builder.Configuration.GetSection("WatchdogConfig"));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
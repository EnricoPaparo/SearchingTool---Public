using Microsoft.EntityFrameworkCore;
using SearchingTool.Services;
using SearchingTool.Models;
using SearchingTool.Data;
using SearchingTool.Utils;
using Serilog;
using Serilog.Formatting.Compact;
using SearchingTool.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
});

// Log file path
var logFilePath = builder.Configuration["LoggingSettings:FilePath"] ?? "Logs/log-.json";

// Disable default logger
builder.Logging.ClearProviders();

// Serilog configuration with readable formatter
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.File(
            path: logFilePath,
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            shared: true,
            formatter: new RenderedCompactJsonFormatter()
        )
        .WriteTo.Console();
});

// App base URL
builder.WebHost.UseUrls("http://localhost:5289");

// Configuration shortcut
var configuration = builder.Configuration;

// Entity Framework DB context
builder.Services.AddDbContextFactory<ScopingReviewContext>(options =>
    options.UseSqlServer(configuration.GetConnectionString("ScopingReviewDb")!));

// Bind configuration section "Paths" to PathsOptions
builder.Services.Configure<PathsOptions>(configuration.GetSection("Paths"));

// MVC controller support + manage JSON serialization loop
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve;
        options.JsonSerializerOptions.WriteIndented = true;
    });

// Core dependencies
builder.Services.AddScoped<PortalResolver>();

// Register search services as ISearchService
builder.Services.AddScoped<ISearchService, PubMedServices>();
builder.Services.AddScoped<ISearchService, ZenodoServices>();
builder.Services.AddScoped<ISearchService, ArxivService>();
builder.Services.AddScoped<ISearchService, ScopusService>();

// PubMed HttpClient
builder.Services.AddHttpClient<PubMedServices>((provider, client) =>
{
    var timeout = int.Parse(configuration["PubMedAPI:Timeout"]!);
    var apiKey = configuration["PubMedAPI:ApiKey"]!;
    client.BaseAddress = new Uri(configuration["PubMedAPI:BaseAddress"]!);
    client.DefaultRequestHeaders.Add("api_key", apiKey);
    client.Timeout = TimeSpan.FromMinutes(timeout);
});

// Zenodo HttpClient
builder.Services.AddHttpClient<ZenodoServices>((provider, client) =>
{
    var timeout = int.Parse(configuration["ZenodoAPI:Timeout"]!);
    var apiKey = configuration["ZenodoAPI:ApiKey"]!;
    client.BaseAddress = new Uri(configuration["ZenodoAPI:BaseAddress"]!);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromMinutes(timeout);
});

// ArXiv HttpClient
builder.Services.AddHttpClient<ArxivService>((provider, client) =>
{
    var timeout = int.Parse(configuration["ArxivAPI:Timeout"]!);
    client.BaseAddress = new Uri(configuration["ArxivAPI:BaseAddress"]!);
    client.Timeout = TimeSpan.FromMinutes(timeout);
});

// Scopus HttpClient
builder.Services.AddHttpClient("Scopus", (provider, client) =>
{
    var timeout = int.Parse(configuration["ScopusAPI:Timeout"]!);
    var apiKey = configuration["ScopusAPI:ApiKey"]!;
    var instToken = configuration["ScopusAPI:InstToken"]!;
    var baseAddress = configuration["ScopusAPI:BaseAddress"]!;

    client.BaseAddress = new Uri(baseAddress);
    client.DefaultRequestHeaders.Add("X-ELS-APIKey", apiKey);
    client.DefaultRequestHeaders.Add("X-ELS-Insttoken", instToken);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromMinutes(timeout);
});

try
{
    Log.Information("Starting application...");

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ScopingReviewContext>();
        await Helper.EnsurePortalsSeededAsync(db);
    }

    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthorization();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal error during application startup.");
}
finally
{
    Log.CloseAndFlush();
}

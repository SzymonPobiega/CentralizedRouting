using CentralRouting.Router;
using CentralRouting.Router.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

//Secrets mounted as files, one per setting, named like the environment variable: /run/secrets/Router__Storage__ConnectionString
var secretsDirectory = builder.Configuration["SecretsDirectory"] ?? "/run/secrets";
builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true);

var settings = builder.Configuration.GetSection("Router").Get<RouterSettings>() ?? new RouterSettings();
var router = settings.ToRouterConfiguration();

builder.Services.AddCentralRouter(router);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapCentralRoutingApi();
app.MapHealthChecks("/health");
app.Run();

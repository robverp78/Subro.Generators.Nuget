using Viaduct.Server;
using Viaduct.Examples;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScopedAndMap<IViaductTestInterface, TestApiImplementation>();

var app = builder.Build();

app.AddRegisteredEndpoints();


app.Run();





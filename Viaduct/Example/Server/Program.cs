using Viaduct.Server;
using Viaduct.Examples;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScopedAndMap<IViaductTestInterface, TestApiImplementation>();

// Closed generic aliases: one registration per record type, both reusing the same generic implementation.
// Generates distinct endpoints: /AddressRecord/CrudGuid/... and /PersonRecord/CrudGuid/...
builder.Services.AddScopedAndMap<ICrudGuid<AddressRecord>, InMemoryCrud<AddressRecord>>();
builder.Services.AddScopedAndMap<ICrudGuid<PersonRecord>, InMemoryCrud<PersonRecord>>();

var app = builder.Build();

app.AddRegisteredEndpoints();


app.Run();





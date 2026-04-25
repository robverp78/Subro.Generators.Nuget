using ClientConsoleExample;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient<IJsonPlaceholderService, JsonPlaceholderService>(client =>
{
    client.BaseAddress = new Uri("https://jsonplaceholder.typicode.com/");
});

using var host = builder.Build();

var api = host.Services.GetRequiredService<IJsonPlaceholderService>();


// Get posts for user 1
Console.WriteLine("=== Posts by User 1 ===");

var posts = await api.GetPostsByUserAsync(userId: 1);
foreach (var post in posts.Take(3))
    Console.WriteLine($"  [{post.Id}] {post.Title}");

Console.WriteLine($"  ... ({posts.Count} posts total)");

// Get completed todos for user 1
Console.WriteLine();
Console.WriteLine("=== Completed Todos for User 1 ===");
var todos = await api.GetTodosByUserAsync(userId: 1, completed: true);
foreach (var todo in todos.Take(5))
    Console.WriteLine($"  [✓] {todo.Title}");

Console.WriteLine($"  ... ({todos.Count} completed todos total)");

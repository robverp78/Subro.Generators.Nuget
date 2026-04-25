using System.Net.Http.Json;

namespace ClientConsoleExample;

public class JsonPlaceholderService(HttpClient httpClient) : IJsonPlaceholderService
{
    public async Task<IReadOnlyList<Post>> GetPostsByUserAsync(int userId, CancellationToken ct = default)
    {
        var posts = await httpClient.GetFromJsonAsync<List<Post>>($"posts?userId={userId}", ct);
        return posts ?? [];
    }

    public async Task<IReadOnlyList<Todo>> GetTodosByUserAsync(int userId, bool? completed = null, CancellationToken ct = default)
    {
        var url = $"todos?userId={userId}";
        if (completed.HasValue)
            url += $"&completed={completed.Value.ToString().ToLowerInvariant()}";

        var todos = await httpClient.GetFromJsonAsync<List<Todo>>(url);
        return todos ?? [];
    }
}

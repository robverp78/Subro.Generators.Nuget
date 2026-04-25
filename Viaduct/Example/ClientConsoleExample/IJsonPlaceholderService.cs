using Viaduct;

namespace ClientConsoleExample;

//[MarkAsJsonSerializable]
public record Post(int UserId, int Id, string Title, string Body);
public record Todo(int UserId, int Id, string Title, bool Completed);

public interface IJsonPlaceholderService
{
    Task<IReadOnlyList<Post>> GetPostsByUserAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<Todo>> GetTodosByUserAsync(int userId, bool? completed = null, CancellationToken ct = default);
}

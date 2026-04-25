namespace Viaduct.Examples
{
    /// <summary>
    /// Basic example interface that can be mapped and called.
    /// NB: this interface is empty of attributes to show that the generator can work with conventions only, but attributes can be added for more control over routes, etc.
    /// </summary>    
    public interface IViaductTestInterface
    {
        public Task ExecuteAsync();
        public Task<int> GetAnIntAsync();
        public void Execute();
        public int GetAnInt();
        public Task<int> GetById(int id);

        public Task CreateUserAsync(int id, string name, DateTime DayOfBirth);

        public Task<TestRecord> GetUserAsync(int id, CancellationToken ct);

        public Task DoSomethingWithUserAsync(TestRecord record);
        public Task DoSomethingWithUserAsync(int id, string description, TestRecord record);
    }

    public record TestRecord(int Id, string Name, DateTime DayOfBirth);

    /// <summary>
    /// A simple implementation of <see cref="IViaductTestInterface"/>. Normally this would be in a separate project that references the shared interface assembly, but for simplicity it's included here.
    /// </summary>
    public class TestApiImplementation : IViaductTestInterface
    {
        public Task ExecuteAsync() => Task.CompletedTask;
        public Task<int> GetAnIntAsync() => Task.FromResult(42);
        public void Execute() { }
        public int GetAnInt() => 42;
        public Task<int> GetById(int id) => Task.FromResult(id * 2);
        public Task CreateUserAsync(int id, string name, DateTime DayOfBirth) => Task.CompletedTask;
        public Task<TestRecord> GetUserAsync(int id, CancellationToken ct = default)
            => Task.FromResult(new TestRecord(id, "TestUser", new DateTime(2000, 1, 1)));
        public Task DoSomethingWithUserAsync(TestRecord record) => Task.CompletedTask;
        public Task DoSomethingWithUserAsync(int id, string description, TestRecord record) => Task.CompletedTask;
    }
}

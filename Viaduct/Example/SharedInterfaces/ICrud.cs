namespace Viaduct.Examples
{
    /// <summary>
    /// Generic CRUD contract. The route resolves the generic type argument, so one contract
    /// serves many record types. Register a closed alias (e.g. <see cref="ICrudGuid{TRecord}"/>)
    /// per record type and both client and server are generated for that closure.
    /// </summary>
    public interface ICrud<TRecord, TKey>
    {
        Task<TKey> CreateNew(TRecord values);
        Task<TRecord> GetById(TKey id);
        Task<TRecord> Update(TKey id, TRecord values);
        Task Delete(TKey id);
    }

    /// <summary>
    /// Strong-key alias: only the record type varies, the key is fixed to <see cref="System.Guid"/>.
    /// Registering <c>ICrudGuid&lt;AddressRecord&gt;</c> yields routes like <c>/AddressRecord/CrudGuid/CreateNew</c>.
    /// </summary>
    public interface ICrudGuid<TRecord> : ICrud<TRecord, Guid> { }

    public record AddressRecord(Guid Id, string Street, string City);

    public record PersonRecord(Guid Id, string Name, DateTime DayOfBirth);

    /// <summary>
    /// Single open-generic implementation registered once via
    /// <c>services.AddScoped(typeof(ICrudGuid&lt;&gt;), typeof(InMemoryCrud&lt;&gt;))</c> covers every record type.
    /// </summary>
    public class InMemoryCrud<TRecord> : ICrudGuid<TRecord>
    {
        public Task<Guid> CreateNew(TRecord values) => Task.FromResult(Guid.NewGuid());
        public Task<TRecord> GetById(Guid id) => Task.FromResult(default(TRecord)!);
        public Task<TRecord> Update(Guid id, TRecord values) => Task.FromResult(values);
        public Task Delete(Guid id) => Task.CompletedTask;
    }
}

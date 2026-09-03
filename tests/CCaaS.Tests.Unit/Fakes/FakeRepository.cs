using System.Linq.Expressions;
using CCaaS.Application.Common;

namespace CCaaS.Tests.Unit.Fakes;

/// <summary>
/// In-memory IRepository&lt;T&gt; for unit-testing Application services WITHOUT a real
/// database - deliberately does not depend on EF Core so these tests build/run without
/// needing SQL Server or the EF InMemory provider.
/// </summary>
public class FakeRepository<T> : IRepository<T> where T : class
{
    public readonly List<T> Items = new();

    public IQueryable<T> Query() => Items.AsQueryable();

    public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var idProperty = typeof(T).GetProperty("Id");
        var match = Items.FirstOrDefault(e => Equals(idProperty?.GetValue(e), id));
        return Task.FromResult(match);
    }

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Task.FromResult(Items.AsQueryable().FirstOrDefault(predicate));

    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Task.FromResult(Items.AsQueryable().Any(predicate));

    public Task<List<T>> ToListAsync(IQueryable<T> query, CancellationToken ct = default)
        => Task.FromResult(query.ToList());

    public Task AddAsync(T entity, CancellationToken ct = default)
    {
        Items.Add(entity);
        return Task.CompletedTask;
    }

    public void Update(T entity) { /* in-memory list already holds the same reference */ }

    public void Remove(T entity) => Items.Remove(entity);
}

public class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }
    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return Task.FromResult(1);
    }
}

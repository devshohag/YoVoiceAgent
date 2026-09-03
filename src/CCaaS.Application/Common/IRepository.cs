using System.Linq.Expressions;

namespace CCaaS.Application.Common;

/// <summary>
/// Persistence-ignorant repository abstraction. Kept deliberately free of any EF Core
/// dependency so the Application layer has ZERO external NuGet package requirements -
/// Infrastructure implements this against CcaasDbContext (see GenericRepository&lt;T&gt;).
/// The global tenant query filter (Section 7 security invariant) is applied inside the
/// Infrastructure implementation, not here - callers never need to think about it.
///
/// Note: FirstOrDefaultAsync/AnyAsync are declared directly on the interface (rather than
/// relying on Microsoft.EntityFrameworkCore's IQueryable extension methods) specifically so
/// this project never needs an EF Core package reference. Query() is still available for
/// read-only composition (e.g. paging, projections) via plain System.Linq.Queryable.
/// </summary>
public interface IRepository<T> where T : class
{
    IQueryable<T> Query();
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<List<T>> ToListAsync(IQueryable<T> query, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

using System.Linq.Expressions;
using CCaaS.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Infrastructure.Persistence;

/// <summary>
/// EF Core-backed implementation of Application's IRepository&lt;T&gt;. The tenant/soft-delete
/// filtering happens automatically via CcaasDbContext's global query filters (see
/// OnModelCreating) - this class never needs to add its own "WHERE TenantId = ..." clause.
/// </summary>
public class GenericRepository<T> : IRepository<T> where T : class
{
    private readonly CcaasDbContext _dbContext;
    private readonly DbSet<T> _set;

    public GenericRepository(CcaasDbContext dbContext)
    {
        _dbContext = dbContext;
        _set = dbContext.Set<T>();
    }

    public IQueryable<T> Query() => _set.AsQueryable();

    // Deliberately NOT using DbSet<T>.FindAsync here: EF Core's Find/FindAsync does not
    // reliably apply global query filters, which would let GetByIdAsync leak another
    // tenant's row by guessing its Id - a direct violation of the Section 7 security
    // invariant. Routing through the normal LINQ path (EF.Property for a by-convention
    // "Id" match) keeps the tenant + soft-delete query filters in effect.
    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _set.FirstOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, ct);

    public async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await _set.FirstOrDefaultAsync(predicate, ct);

    public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await _set.AnyAsync(predicate, ct);

    public async Task<List<T>> ToListAsync(IQueryable<T> query, CancellationToken ct = default)
        => await query.ToListAsync(ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await _set.AddAsync(entity, ct);

    public void Update(T entity) => _set.Update(entity);

    public void Remove(T entity) => _set.Remove(entity);
}

public class UnitOfWork : IUnitOfWork
{
    private readonly CcaasDbContext _dbContext;

    public UnitOfWork(CcaasDbContext dbContext) => _dbContext = dbContext;

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _dbContext.SaveChangesAsync(ct);
}

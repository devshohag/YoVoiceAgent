using CCaaS.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CCaaS.Tests.Unit.Appointment;

public sealed class AppointmentSqlFactAttribute : FactAttribute
{
    public AppointmentSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CCAAS_TASK25_SQL")))
            Skip = "Set CCAAS_TASK25_SQL to a disposable SQL Server; the dedicated PR workflow runs these tests.";
    }
}

public sealed class AppointmentSqlFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("CCAAS_TASK25_SQL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        // Always use a new database, never the database named by the supplied connection string.
        ConnectionString = IsolatedConnection(configured);
        await using var db = Open();
        await db.Database.EnsureCreatedAsync();
    }

    public CcaasDbContext Open(params IInterceptor[] interceptors) => new(Options(ConnectionString, interceptors));

    public static string IsolatedConnection(string configured) => new SqlConnectionStringBuilder(configured)
    {
        InitialCatalog = "CCaaS_Task25_" + Guid.NewGuid().ToString("N")
    }.ConnectionString;

    public static DbContextOptions<CcaasDbContext> Options(string connection, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<CcaasDbContext>()
            .UseSqlServer(connection, sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(2), null))
            .AddInterceptors(interceptors).Options;

    // CI destroys the disposable container. Local test databases are retained for inspection;
    // no DROP DATABASE or EnsureDeleted is ever run against a supplied server.
    public Task DisposeAsync() => Task.CompletedTask;
}

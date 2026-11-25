using Microsoft.EntityFrameworkCore;
using Npgsql;
using Omro.Domain.Exceptions;
using Omro.Domain.Repositories;

namespace Omro.Infrastructure.Persistence.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private const string ActiveTaskUniqueIndexName = "IX_RESOLUTION_TASK_active_dedup";

    private readonly ApplicationDbContext _dbContext;
    
    public UnitOfWork(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsActiveTaskUniqueViolation(ex))
        {
            _dbContext.ChangeTracker.Clear();
            throw new DuplicateActiveTaskException(
                "Conflicto concurrente al registrar tarea activa de resolución.",
                ex);
        }
    }

    private static bool IsActiveTaskUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg &&
        pg.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(pg.ConstraintName, ActiveTaskUniqueIndexName, StringComparison.OrdinalIgnoreCase);
}
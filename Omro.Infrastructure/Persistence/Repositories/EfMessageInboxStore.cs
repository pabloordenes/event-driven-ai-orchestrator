using Microsoft.EntityFrameworkCore;
using Omro.Application.Interfaces;
using Omro.Domain.Messaging;

namespace Omro.Infrastructure.Persistence.Repositories;

public class EfMessageInboxStore : IMessageInboxStore
{
    private readonly ApplicationDbContext _dbContext;

    public EfMessageInboxStore(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> HasBeenCompletedAsync(
        Guid messageId,
        string consumerName,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(
                m => m.MessageId == messageId &&
                     m.ConsumerName == consumerName &&
                     m.Status == ProcessedMessage.StatusCompleted,
                cancellationToken);
    }

    public async Task MarkCompletedAsync(
        Guid messageId,
        string consumerName,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var existing = await _dbContext.ProcessedMessages
            .FirstOrDefaultAsync(
                m => m.MessageId == messageId && m.ConsumerName == consumerName,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.Status != ProcessedMessage.StatusCompleted)
            {
                existing.MarkCompleted(utcNow, retention);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var entry = ProcessedMessage.CreateCompleted(messageId, consumerName, retention, utcNow);
        _dbContext.ProcessedMessages.Add(entry);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(entry).State = EntityState.Detached;
        }
    }
}

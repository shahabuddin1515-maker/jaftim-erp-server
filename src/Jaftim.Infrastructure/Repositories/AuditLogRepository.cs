using System.Data;
using Dapper;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Audit;
using Jaftim.Domain.Entities.Audit;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>
/// AuditLog_* procedures (database/v2/002_AuditLog.sql). The writer runs on a background thread with the tenant
/// bound explicitly by the flushing scope, so this repository is tenant-bound like every other one.
/// </summary>
public sealed class AuditLogRepository(IDbExecutor db) : IAuditLogRepository
{
    public Task WriteAsync(AuditRecord r, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("AuditLog_Write")
                .With("@AtUtc", r.AtUtc, DbType.DateTime2)
                .With("@ActorUserProfileId", r.ActorUserProfileId)
                .With("@ActorAccountId", r.ActorAccountId, DbType.String, 450)
                .With("@Action", r.Action, DbType.String, 100)
                .With("@EntityType", r.EntityType, DbType.String, 100)
                .With("@EntityId", r.EntityId)
                .With("@Before", r.BeforeJson)
                .With("@After", r.AfterJson)
                .With("@CorrelationId", r.CorrelationId)
                .With("@Ip", r.Ip, DbType.String, 64)
                .With("@UserAgent", r.UserAgent, DbType.String, 400)
                .WithoutAudit(), ct);

    public Task<PagedResult<AuditEntry>> GetByEntityAsync(string entityType, long? entityId, PagedRequest paging, CancellationToken ct = default) =>
        db.QueryMultipleAsync(
            SpCall.Procedure("AuditLog_GetByEntity")
                .With("@EntityType", entityType, DbType.String, 100)
                .With("@EntityId", entityId)
                .With("@Skip", paging.Skip)
                .With("@Take", paging.PageSize)
                .WithoutAudit(),
            async grid =>
            {
                int total = await grid.ReadSingleAsync<int>();
                IReadOnlyList<AuditEntry> rows = (await grid.ReadAsync<AuditEntry>()).AsList();
                return new PagedResult<AuditEntry>(rows, paging.Page, paging.PageSize, total);
            }, ct);
}

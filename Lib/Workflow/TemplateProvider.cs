using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Configuration;

namespace OrderManager.Backend.Lib.Workflow;

public enum TemplateKind
{
    /// <summary>Order-level main process — validates order transitions.</summary>
    Process,

    /// <summary>Factory production steps — validates line-item transitions.</summary>
    ProductionStep,
}

public interface ITemplateProvider
{
    Task<WorkflowTemplate> GetActiveAsync(TemplateKind kind);
}

/// <summary>
/// Loads the active template for the configured client and caches it for the lifetime
/// of the worker instance.
///
/// IMPORTANT — TEMPLATE CHANGES REQUIRE A REDEPLOY TO TAKE EFFECT. This is intended
/// behaviour, not a bug or a missing cache-invalidation feature. Template changes go
/// through client approval and a dev-initiated redeploy, and a redeploy cycles every
/// worker instance, which is what clears this cache.
///
/// The corollary matters: because the cache is per *instance* and Flex Consumption
/// scales instances in and out, editing a template row in SQL *without* redeploying
/// leaves old instances serving the old rules while newly-started instances serve the
/// new ones — the same request can be validated differently depending on which
/// instance handles it. Always pair a template edit with a redeploy.
///
/// See also CLAUDE.md §5 and sql/004_workflow_engine.sql.
/// </summary>
public sealed class TemplateProvider : ITemplateProvider
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly Guid _clientId;
    private readonly ConcurrentDictionary<(TemplateKind Kind, Guid ClientId), Lazy<Task<WorkflowTemplate>>> _cache = new();

    public TemplateProvider(ISqlConnectionFactory connectionFactory, IConfiguration configuration)
    {
        _connectionFactory = connectionFactory;

        var configured = configuration["CLIENT_ID"]
            ?? throw new InvalidOperationException("CLIENT_ID is not configured");
        _clientId = Guid.Parse(configured);
    }

    public async Task<WorkflowTemplate> GetActiveAsync(TemplateKind kind)
    {
        var key = (kind, _clientId);

        var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<WorkflowTemplate>>(
            () => LoadAsync(kind),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value;
        }
        catch
        {
            // Don't let a transient failure (e.g. the serverless DB resuming from
            // auto-pause) poison the cache with a permanently faulted task.
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<WorkflowTemplate> LoadAsync(TemplateKind kind)
    {
        // Table name comes from the enum, never from user input.
        var table = kind switch
        {
            TemplateKind.Process => "process_templates",
            TemplateKind.ProductionStep => "production_step_templates",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        using var connection = _connectionFactory.CreateConnection();

        var json = await connection.QuerySingleOrDefaultAsync<string>(
            $"SELECT template_json FROM {table} WHERE client_id = @ClientId AND active = 1",
            new { ClientId = _clientId });

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"No active {table} row for client {_clientId}");
        }

        return WorkflowTemplate.Parse(json);
    }
}

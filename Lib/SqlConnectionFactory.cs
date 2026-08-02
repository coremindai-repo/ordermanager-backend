using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace OrderManager.Backend.Lib;

public interface ISqlConnectionFactory
{
    SqlConnection CreateConnection();
}

public class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration["SQL_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("SQL_CONNECTION_STRING is not configured");
    }

    public SqlConnection CreateConnection() => new(_connectionString);
}

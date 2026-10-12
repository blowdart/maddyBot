// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;

using Npgsql;

namespace idunno.Bluesky.BotSdk.DataProviders;

/// <summary>Registers the application's chosen durable database provider in dependency injection.</summary>
public static class BotStorageExtensions
{
    /// <summary>Registers PostgreSQL storage and a host-owned Npgsql connection pool.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="connectionString">The PostgreSQL connection string with bounded timeouts and appropriate TLS.</param>
    /// <param name="caCertificate">The optional trusted PEM server CA certificate.</param>
    /// <returns>The service collection for further configuration.</returns>
    public static IServiceCollection AddPostgreSqlBotStorage(
        this IServiceCollection services,
        string connectionString,
        string? caCertificate = null)
    {
        CheckRegistration(services, connectionString);
        services.AddSingleton(_ =>
        {
            NpgsqlDataSourceBuilder source = new(connectionString);
            if (!string.IsNullOrWhiteSpace(caCertificate))
            {
                source.UseRootCertificate(X509Certificate2.CreateFromPem(caCertificate));
            }

            return source.Build();
        });
        services.AddSingleton<IDatabaseProvider, PostgreSqlDatabaseProvider>();

        return services;
    }

    /// <summary>Registers MySQL 8.4 storage using bounded connections and InnoDB lease fencing.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="connectionString">The MySQL connection string; use verified TLS outside local development.</param>
    /// <returns>The service collection for further configuration.</returns>
    public static IServiceCollection AddMySqlBotStorage(this IServiceCollection services, string connectionString)
    {
        CheckRegistration(services, connectionString);
        services.AddSingleton<IDatabaseProvider>(provider => new MySqlDatabaseProvider(
            connectionString,
            provider.GetRequiredService<BotOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<MySqlDatabaseProvider>>()));

        return services;
    }

    /// <summary>Registers SQLite storage in a persistent local file shared by processes on one host.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="connectionString">The SQLite connection string pointing to a persistent local file.</param>
    /// <returns>The service collection for further configuration.</returns>
    public static IServiceCollection AddSqliteBotStorage(this IServiceCollection services, string connectionString)
    {
        CheckRegistration(services, connectionString);
        services.AddSingleton<IDatabaseProvider>(provider => new SqliteDatabaseProvider(
            connectionString,
            provider.GetRequiredService<BotOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<SqliteDatabaseProvider>>()));

        return services;
    }

    /// <summary>Rejects missing configuration and multiple storage providers before constructing the host.</summary>
    /// <param name="services">The application's existing DI registrations.</param>
    /// <param name="connectionString">The required connection string for the chosen provider.</param>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    /// <exception cref="ArgumentException">The connection string is empty.</exception>
    /// <exception cref="InvalidOperationException">A database provider is already registered.</exception>
    private static void CheckRegistration(IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (services.Any(service => service.ServiceType == typeof(IDatabaseProvider)))
        {
            throw new InvalidOperationException("A bot database provider is already registered. Register exactly one storage provider.");
        }
    }
}
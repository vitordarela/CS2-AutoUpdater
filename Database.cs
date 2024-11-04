using System.Reflection.Metadata;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using MySqlConnector;

namespace AutoUpdater;

public class Database
{
    private readonly AutoUpdater autoUpdater;
    private readonly string dbConnectionString;

    public Database(AutoUpdater autoUpdater, string dbConnectionString)
    {
        this.autoUpdater = autoUpdater;
        this.dbConnectionString = dbConnectionString;
    }

    public async Task UpdateVersionContainer(string containerName, string version, bool serverUpdated)
    {
        try
        {
            await using var connection = new MySqlConnection(this.dbConnectionString);
            await connection.OpenAsync();

            var updateQuery = $@"
        UPDATE 
            `{this.autoUpdater.Config.MySQLTableName}` 
        SET 
            `app_version` = @app_version,
            `updated` = @updated

        WHERE 
            `container_name` = @container_name";

            await connection.ExecuteAsync(updateQuery, new
            {
                container_name = containerName,
                app_version = version,
                updated = serverUpdated,
            });
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    public async Task UpdateUpdatedFlag(string containerName, bool serverUpdated)
    {
        try
        {
            await using var connection = new MySqlConnection(this.dbConnectionString);
            await connection.OpenAsync();

            var updateQuery = $@"
        UPDATE 
            `{this.autoUpdater.Config.MySQLTableName}` 
        SET 
            `updated` = @updated

        WHERE 
            `container_name` = @container_name";

            var response = await connection.ExecuteAsync(updateQuery, new
            {
                container_name = containerName,
                updated = serverUpdated,
            });

            Console.WriteLine($"Updated flag for {containerName} to {serverUpdated} Response: {response}");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    public async Task AddContainerToDb(string containerName, string version)
    {
        try
        {
            await using var connection = new MySqlConnection(this.dbConnectionString);
            await connection.OpenAsync();

            var parameters = new
            {
                container_name = containerName,
                app_version = version,
                updated = true,
            };

            var query = $@"
                INSERT INTO `{this.autoUpdater.Config.MySQLTableName}` 
                (`container_name`, `app_version`, `updated`) 
                VALUES 
                (@container_name, @app_version, @updated);";

            await connection.ExecuteAsync(query, parameters);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    public async Task CreateTable()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(this.dbConnectionString);
            dbConnection.Open();

            var createLrTable = $@"
            CREATE TABLE IF NOT EXISTS `{this.autoUpdater.Config.MySQLTableName}` (
                `container_name` VARCHAR(255) NOT NULL,
                `app_version` VARCHAR(255) NOT NULL,
                `updated` BOOLEAN NOT NULL,
                `creation_date` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `last_update` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            );";

            await dbConnection.ExecuteAsync(createLrTable);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    public async Task<bool> CheckContainersUpdateing()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(this.dbConnectionString);
            dbConnection.Open();

            var sql = $@"SELECT 
                            EXISTS (
                                SELECT 1 
                                FROM `{this.autoUpdater.Config.MySQLTableName}` 
                                WHERE updated = false
                            ) AS is_updating;";

            bool hasPendingUpdate = await dbConnection.QueryFirstOrDefaultAsync<bool>(sql);
            return hasPendingUpdate;

        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }

    }

    public async Task<Container?> GetContainer(string containerName)
    {
        try
        {
            await using var connection = new MySqlConnection(this.dbConnectionString);
            await connection.OpenAsync();
            var container = await connection.QueryFirstOrDefaultAsync<Container>(
                $"SELECT * FROM `{this.autoUpdater.Config.MySQLTableName}` WHERE `container_name` = @ContainerName",
               new
               {
                   ContainerName = containerName,
               });

            return container;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return null;
    }

}
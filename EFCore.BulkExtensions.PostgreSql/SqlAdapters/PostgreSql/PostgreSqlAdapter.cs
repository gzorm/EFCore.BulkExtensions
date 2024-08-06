﻿using Microsoft.EntityFrameworkCore;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EFCore.BulkExtensions.SqlAdapters.PostgreSql;

/// <inheritdoc/>
public class PostgreSqlAdapter : ISqlOperationsAdapter
{
    private PostgreSqlQueryBuilder ProviderSqlQueryBuilder => new PostgreSqlQueryBuilder();

    /// <inheritdoc/>
    #region Methods
    // Insert
    public void Insert<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, Action<decimal>? progress)
    {
        InsertAsync(context, entities, tableInfo, progress, isAsync: false, CancellationToken.None).GetAwaiter().GetResult();
    }
    
    /// <inheritdoc/>
    public async Task InsertAsync<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, Action<decimal>? progress,
        CancellationToken cancellationToken)
    {
        await InsertAsync(context, entities, tableInfo, progress, isAsync: true, cancellationToken).ConfigureAwait(false);
    }
    
    /// <inheritdoc/>
    protected static async Task InsertAsync<T>(DbContext context, IEnumerable<T> entities, TableInfo tableInfo, Action<decimal>? progress, bool isAsync,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = (NpgsqlConnection?)SqlAdaptersMapping.DbServer.DbConnection; // TODO refactor
        bool closeConnectionInternally = false;
        if (connection == null)
        {
            (var dbConnection, closeConnectionInternally) = await OpenAndGetNpgsqlConnectionAsync(context, isAsync, cancellationToken).ConfigureAwait(false);
            connection = (NpgsqlConnection)dbConnection;
        }

        try
        {
            var operationType = tableInfo.InsertToTempTable ? OperationType.InsertOrUpdate : OperationType.Insert;
            string sqlCopy = PostgreSqlQueryBuilder.InsertIntoTable(tableInfo, operationType);

            using var writer = isAsync ? await connection.BeginBinaryImportAsync(sqlCopy, cancellationToken).ConfigureAwait(false)
                                       : connection.BeginBinaryImport(sqlCopy);

            var uniqueColumnName = tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList().FirstOrDefault();

            var doKeepIdentity = tableInfo.BulkConfig.SqlBulkCopyOptions == SqlBulkCopyOptions.KeepIdentity;

            var propertiesColumnDict = ((tableInfo.InsertToTempTable || doKeepIdentity) && tableInfo.IdentityColumnName == uniqueColumnName)
                ? tableInfo.PropertyColumnNamesDict
                : tableInfo.PropertyColumnNamesDict.Where(a => a.Value != tableInfo.IdentityColumnName);

            var propertiesNames = propertiesColumnDict.Select(a => a.Key).ToList();
            var entitiesCopiedCount = 0;
            foreach (var entity in entities)
            {
                if (isAsync)
                {
                    await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    writer.StartRow();
                }

                foreach (var propertyName in propertiesNames)
                {
                    if (operationType == OperationType.Insert
                        && tableInfo.DefaultValueProperties.Contains(propertyName) 
                        && !tableInfo.PrimaryKeysPropertyColumnNameDict.ContainsKey(propertyName))
                    {
                        continue;
                    }

                    var propertyValue = GetPropertyValue(context, tableInfo, propertyName, entity);
                    var propertyColumnName = tableInfo.PropertyColumnNamesDict.GetValueOrDefault(propertyName, "");
                    var columnType = tableInfo.ColumnNamesTypesDict[propertyColumnName];

                    // string is 'text' which works fine
                    if (columnType.StartsWith("character"))   // when MaxLength is defined: 'character(1)' or 'character varying'
                        columnType = "character";             // 'character' is like 'string'
                    else if (columnType.StartsWith("varchar"))
                        columnType = "varchar";
                    else if (columnType.StartsWith("numeric") && columnType != "numeric[]")
                        columnType = "numeric";

                    if (columnType.StartsWith("timestamp(")) // timestamp(n) | len:12 // TEST: TimeStamp2PGTest
                        columnType = "timestamp" + columnType.Substring(12, columnType.Length - 12);

                    if (columnType.StartsWith("geometry"))
                        columnType = "geometry";
                    if (columnType.StartsWith("geography"))
                        columnType = "geography";

                    var convertibleDict = tableInfo.ConvertibleColumnConverterDict;
                    if (convertibleDict.TryGetValue(propertyColumnName, out var converter))
                    {
                        if (propertyValue != null)
                        {
                            if (converter.ModelClrType.IsEnum)
                            {
                                var clrType = converter.ProviderClrType;
                                if (clrType == typeof(byte)) // columnType == "smallint"
                                    propertyValue = (byte)propertyValue;
                                if (clrType == typeof(short))
                                    propertyValue = (short)propertyValue;
                                if (clrType == typeof(int))
                                    propertyValue = (int)propertyValue;
                                if (clrType == typeof(long))
                                    propertyValue = (long)propertyValue;
                                if (clrType == typeof(string))
                                    propertyValue = propertyValue.ToString();
                            }
                            else
                            {
                                try
                                {
                                    propertyValue = converter.ConvertToProvider.Invoke(propertyValue);
                                }
                                catch (InvalidCastException ex)
                                {   
                                    // fix for case when PK(Id) if String type with converter and is encapsulated to private sealed class with constructor;
                                    // just need to skip converter call as value is already loaded into underlaying field. (Test: ConverterStringPKTest), [issue: #1343]
                                    if (!ex.Message.StartsWith("Invalid cast from 'System.String'"))
                                        throw;
                                }
                            }
                        }
                    }

                    if (isAsync)
                    {
                        await writer.WriteAsync(propertyValue, columnType, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        writer.Write(propertyValue, columnType);
                    }
                }
                entitiesCopiedCount++;
                if (progress != null && entitiesCopiedCount % tableInfo.BulkConfig.NotifyAfter == 0)
                {
                    progress?.Invoke(ProgressHelper.GetProgress(entities.Count(), entitiesCopiedCount));
                }
            }
            if (isAsync)
            {
                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                writer.Complete();
            }
        }
        finally
        {
            if (closeConnectionInternally)
            {
                if (isAsync)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
                else
                {
                    connection.Close();
                }
            }
        }
    }

    static object? GetPropertyValue<T>(DbContext context, TableInfo tableInfo, string propertyName, T entity)
    {
        if (!tableInfo.FastPropertyDict.ContainsKey(propertyName.Replace('.', '_')) || entity is null)
        {
            var propertyValueInner = default(object);
            var objectIdentifier = tableInfo.ObjectIdentifier;
            var shadowPropertyColumnNamesDict = tableInfo.ColumnToPropertyDictionary
                .Where(a => a.Value.IsShadowProperty()).ToDictionary(a => a.Value.Name, a => a.Value.GetColumnName(objectIdentifier));
            if (shadowPropertyColumnNamesDict.ContainsKey(propertyName))
            {
                if (tableInfo.BulkConfig.ShadowPropertyValue == null)
                {
                    propertyValueInner = context.Entry(entity!).Property(propertyName).CurrentValue;
                }
                else
                {
                    propertyValueInner = tableInfo.BulkConfig.ShadowPropertyValue(entity!, propertyName);
                }

                if (tableInfo.ConvertibleColumnConverterDict.ContainsKey(propertyName))
                {
                    propertyValueInner = tableInfo.ConvertibleColumnConverterDict[propertyName].ConvertToProvider.Invoke(propertyValueInner);
                }
                return propertyValueInner;
            }
            return null;
        }

        object? propertyValue = entity;
        string fullPropertyName = string.Empty;
        foreach (var entry in propertyName.AsSpan().Split("."))
        {
            if (propertyValue == null)
            {
                return null;
            }

            if (fullPropertyName.Length > 0)
            {
                fullPropertyName += $"_{entry.Token}";
            }
            else
            {
                fullPropertyName = new string(entry.Token);
            }
            
            propertyValue = tableInfo.FastPropertyDict[fullPropertyName].Get(propertyValue);
        }
        return propertyValue;
    }

    /// <inheritdoc/>
    public void Merge<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, OperationType operationType, Action<decimal>? progress) 
        where T : class
    {
        MergeAsync(context, type, entities, tableInfo, operationType, progress, isAsync: false, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public async Task MergeAsync<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, OperationType operationType, Action<decimal>? progress,
        CancellationToken cancellationToken) where T : class
    {
        await MergeAsync(context, type, entities, tableInfo, operationType, progress, isAsync: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected async Task MergeAsync<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, OperationType operationType, Action<decimal>? progress,
        bool isAsync, CancellationToken cancellationToken) where T : class
    {
        bool tempTableCreated = false;
        bool outputTableCreated = false;
        bool uniqueIndexCreated = false;
        bool connectionOpenedInternally = false;
        try
        {
            if (tableInfo.BulkConfig.CustomSourceTableName == null)
            {
                tableInfo.InsertToTempTable = true;

                var sqlCreateTableCopy = PostgreSqlQueryBuilder.CreateTableCopy(tableInfo.FullTableName, tableInfo.FullTempTableName, tableInfo.BulkConfig.UseTempDB);
                if (isAsync)
                {
                    await context.Database.ExecuteSqlRawAsync(sqlCreateTableCopy, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    context.Database.ExecuteSqlRaw(sqlCreateTableCopy);
                }
                tempTableCreated = true;
            }

            if (tableInfo.BulkConfig.CalculateStats)
            {
                var sqlCreateOutputTableCopy = PostgreSqlQueryBuilder.CreateOutputStatsTable(tableInfo.FullTempOutputTableName, tableInfo.BulkConfig.UseTempDB);
                if (isAsync)
                {
                    await context.Database.ExecuteSqlRawAsync(sqlCreateOutputTableCopy, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    context.Database.ExecuteSqlRaw(sqlCreateOutputTableCopy);
                }
                outputTableCreated = true;
            }

            bool hasUniqueIndex = false;
            string joinedEntityPK = string.Join("_", tableInfo.EntityPKPropertyColumnNameDict.Keys.ToList());
            string joinedPrimaryKeys = string.Join("_", tableInfo.PrimaryKeysPropertyColumnNameDict.Keys.ToList());
            if (joinedEntityPK == joinedPrimaryKeys)
            {
                hasUniqueIndex = true; // Explicit Constrain not required for PK
            }
            else
            {
                (hasUniqueIndex, connectionOpenedInternally) = await CheckHasExplicitUniqueConstrainAsync(context, tableInfo, isAsync, cancellationToken).ConfigureAwait(false);
            }

            if (!hasUniqueIndex)
            {
                string createUniqueIndex = PostgreSqlQueryBuilder.CreateUniqueIndex(tableInfo);
                string createUniqueConstrain = PostgreSqlQueryBuilder.CreateUniqueConstrain(tableInfo);
                if (isAsync)
                {
                    await context.Database.ExecuteSqlRawAsync(createUniqueIndex, cancellationToken).ConfigureAwait(false);
                    //await context.Database.ExecuteSqlRawAsync(createUniqueConstrain, cancellationToken).ConfigureAwait(false); // UniqueConstrain Not needed
                }
                else
                {
                    context.Database.ExecuteSqlRaw(createUniqueIndex);
                    //context.Database.ExecuteSqlRaw(createUniqueConstrain); // UniqueConstrain Not needed
                }
                uniqueIndexCreated = true;
            }

            if (tableInfo.BulkConfig.CustomSourceTableName == null)
            {
                if (isAsync)
                {
                    await InsertAsync(context, type, entities, tableInfo, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Insert(context, type, entities, tableInfo, progress);
                }
            }

            var sqlMergeTable = PostgreSqlQueryBuilder.MergeTable<T>(tableInfo, operationType);
            if (operationType != OperationType.Read && (!tableInfo.BulkConfig.SetOutputIdentity || operationType == OperationType.Delete))
            {
                if (isAsync)
                {
                    await context.Database.ExecuteSqlRawAsync(sqlMergeTable, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    context.Database.ExecuteSqlRaw(sqlMergeTable);
                }
            }
            else
            {
                var sqlMergeTableOutput = sqlMergeTable.TrimEnd(';');                                         // When ends with ';' test OwnedTypes throws ex at LoadOutputEntities:
                List<T> outputEntities = tableInfo.LoadOutputEntities<T>(context, type, sqlMergeTableOutput); // postgresql '42601: syntax error at or near ";"
                tableInfo.UpdateReadEntities(entities, outputEntities, context);
            }

            if (tableInfo.BulkConfig.CalculateStats)
            {
                int numberInserted;
                if (isAsync)
                {
                    numberInserted = await GetStatsNumbersPGAsync(context, tableInfo, isAsync: true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    numberInserted = GetStatsNumbersPGAsync(context, tableInfo, isAsync: false, cancellationToken).GetAwaiter().GetResult();
                }
                tableInfo.BulkConfig.StatsInfo = new StatsInfo
                {
                    StatsNumberInserted = numberInserted,
                    StatsNumberUpdated = entities.Count() - numberInserted,
                };
            }
        }
        finally
        {
            try
            {
                if (uniqueIndexCreated)
                {
                    string dropUniqueIndex = PostgreSqlQueryBuilder.DropUniqueIndex(tableInfo);
                    if (isAsync)
                    {
                        await context.Database.ExecuteSqlRawAsync(dropUniqueIndex, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        context.Database.ExecuteSqlRaw(dropUniqueIndex);
                    }
                }

                if (!tableInfo.BulkConfig.UseTempDB)
                {
                    if (outputTableCreated)
                    {
                        var sqlDropOutputTable = PostgreSqlQueryBuilder.DropTable(tableInfo.FullTempOutputTableName);
                        if (isAsync)
                        {
                            await context.Database.ExecuteSqlRawAsync(sqlDropOutputTable, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            context.Database.ExecuteSqlRaw(sqlDropOutputTable);
                        }
                    }

                    if (tempTableCreated)
                    {
                        var sqlDropTable = PostgreSqlQueryBuilder.DropTable(tableInfo.FullTempTableName);
                        if (isAsync)
                        {
                            await context.Database.ExecuteSqlRawAsync(sqlDropTable, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            context.Database.ExecuteSqlRaw(sqlDropTable);
                        }
                    }
                }
            }
            catch (PostgresException ex) when (ex.SqlState == "25P02")
            {
                // ignore "current transaction is aborted" exception as it hides the real exception that caused it
            }

            if (connectionOpenedInternally)
            {
                var connection = (NpgsqlConnection)context.Database.GetDbConnection();
                if (isAsync)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
                else
                {
                    connection.Close();
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Read<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, Action<decimal>? progress) where T : class
        => ReadAsync(context, type, entities, tableInfo, progress, isAsync: false, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task ReadAsync<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, Action<decimal>? progress, CancellationToken cancellationToken) where T : class
        =>  await ReadAsync(context, type, entities, tableInfo, progress, isAsync: true, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    protected async Task ReadAsync<T>(DbContext context, Type type, IEnumerable<T> entities, TableInfo tableInfo, Action<decimal>? progress, bool isAsync, CancellationToken cancellationToken) where T : class
        =>  await MergeAsync(context, type, entities, tableInfo, OperationType.Read, progress, isAsync, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public void Truncate(DbContext context, TableInfo tableInfo)
    {
        var sqlTruncateTable = new PostgreSqlQueryBuilder().TruncateTable(tableInfo.FullTableName);
        context.Database.ExecuteSqlRaw(sqlTruncateTable);
    }

    /// <inheritdoc/>
    public async Task TruncateAsync(DbContext context, TableInfo tableInfo, CancellationToken cancellationToken)
    {
        var sqlTruncateTable = ProviderSqlQueryBuilder.TruncateTable(tableInfo.FullTableName);
        await context.Database.ExecuteSqlRawAsync(sqlTruncateTable, cancellationToken).ConfigureAwait(false);
    }
    #endregion

    #region Connection
    internal static async Task<(DbConnection, bool)> OpenAndGetNpgsqlConnectionAsync(DbContext context,
        bool isAsync, CancellationToken cancellationToken)
    {
        bool oonnectionOpenedInternally = false;
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            if (isAsync)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connection.Open();
            }
            oonnectionOpenedInternally = true;
        }
        return (connection, oonnectionOpenedInternally);
    }

    internal static async Task<(bool, bool)> CheckHasExplicitUniqueConstrainAsync(DbContext context, TableInfo tableInfo,
        bool isAsync, CancellationToken cancellationToken)
    {
        string countUniqueConstrain = PostgreSqlQueryBuilder.CountUniqueConstrain(tableInfo);
        
        (DbConnection connection, bool connectionOpenedInternally) = await OpenAndGetNpgsqlConnectionAsync(context, isAsync, cancellationToken).ConfigureAwait(false);
        bool hasUniqueConstrain = false;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = countUniqueConstrain;

            if (isAsync)
            {
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (reader.HasRows)
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        hasUniqueConstrain = (long)reader[0] == 1;
                    }
                }
            }
            else
            {
                using var reader = command.ExecuteReader();
                if (reader.HasRows)
                {
                    while (reader.Read())
                    {
                        hasUniqueConstrain = (long)reader[0] == 1;
                    }
                }
            }
        }
        return (hasUniqueConstrain, connectionOpenedInternally);
    }
    #endregion

    #region SqlCommands
    /// <summary>
    /// Gets the Stats numbers of entities
    /// </summary>
    /// <param name="context"></param>
    /// <param name="tableInfo"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="isAsync"></param>
    /// <returns></returns>
    public static async Task<int> GetStatsNumbersPGAsync(DbContext context, TableInfo tableInfo, bool isAsync, CancellationToken cancellationToken)
    {
        var sqlQuery = @$"SELECT COUNT(*) FROM {tableInfo.FullTempOutputTableName} WHERE ""xmaxNumber"" = 0;";
        sqlQuery = sqlQuery.Replace("[", @"""").Replace("]", @"""");

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        bool doExplicitCommit = false;
        long counter = 0;

        try
        {
            var command = connection.CreateCommand();

            if (context.Database.CurrentTransaction == null)
            {
                doExplicitCommit = true;
            }

            var dbTransaction = doExplicitCommit ? connection.BeginTransaction()
                                                 : context.Database.CurrentTransaction?.GetUnderlyingTransaction(tableInfo.BulkConfig);
            var transaction = (NpgsqlTransaction?)dbTransaction;

            command.CommandText = sqlQuery;

            object? scalar = isAsync ? await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                                           : command.ExecuteScalar();
            counter = (long?)scalar ?? 0;

            if (doExplicitCommit)
            {
                transaction?.Commit();
            }
        }
        finally
        {
            if (isAsync)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            else
            {
                context.Database.CloseConnection();
            }
        }
        return (int)counter;
    }
    #endregion

    /// <summary>
    /// Sets provider specific config in TableInfo
    /// </summary>
    /// <param name="context"></param>
    /// <param name="tableInfo"></param>
    /// <returns></returns>
    public string? ReconfigureTableInfo(DbContext context, TableInfo tableInfo)
    {
        var defaultSchema = "public";
        var csb = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        if (!string.IsNullOrWhiteSpace(csb.SearchPath))
        {
            defaultSchema = csb.SearchPath.Split(',')[0];
        }
        return defaultSchema;
    }
}

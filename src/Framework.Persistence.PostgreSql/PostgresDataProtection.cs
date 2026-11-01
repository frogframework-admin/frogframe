using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Persists ASP.NET Core Data Protection keys in PostgreSQL table docframe_data_protection_keys.
/// Ensures session cookies, antiforgery tokens, and authentication tickets remain valid
/// across server restarts and horizontal scaling across multiple instances.
/// </summary>
public sealed class PostgresXmlRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();
        try
        {
            using var conn = dataSource.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT xml FROM docframe_data_protection_keys ORDER BY id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var xml = reader.GetString(0);
                elements.Add(XElement.Parse(xml));
            }
        }
        catch
        {
            // Table might not exist yet if called before migrations execute
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO docframe_data_protection_keys (friendly_name, xml)
            VALUES ($1, $2)
            ON CONFLICT (friendly_name) DO UPDATE SET xml = EXCLUDED.xml";
        cmd.Parameters.AddWithValue(friendlyName);
        cmd.Parameters.AddWithValue(element.ToString(SaveOptions.DisableFormatting));
        cmd.ExecuteNonQuery();
    }
}


using Framework.Documents;
using Framework.Metadata;

namespace Framework.Persistence;

public sealed record DataMigrationResult(int DocumentsUpdated);
public sealed record DataMigrationPreviewResult(bool Valid, int Documents, string? Error = null);

public interface IDocTypeMigrationStore
{
    DataMigrationResult MigrateAndPublish(MetadataVersion version, CompiledDocType source, CompiledDocType target,
        int expectedDocuments, int maxDocuments);
}

public static class DocTypeDataMigrator
{
    public static (Document Document, IReadOnlyList<FieldChange> Changes) Transform(
        Document source, CompiledDocType targetMeta, IReadOnlyList<DataMigrationOperationDef> operations, string actor, DateTimeOffset now)
    {
        var migrated = source.Clone();
        var changes = new List<FieldChange>();
        foreach (var operation in operations)
        {
            switch (operation.Type)
            {
                case "RenameField":
                    if (!migrated.Data.TryGetValue(operation.FromField!, out var oldValue)) break;
                    if (migrated.Data.TryGetValue(operation.ToField!, out var existing) && existing is not null)
                        throw new InvalidOperationException($"Dokumen {source.Name}: field '{operation.ToField}' sudah memiliki nilai; rename dibatalkan.");
                    migrated.Data[operation.ToField!] = oldValue;
                    migrated.Data.Remove(operation.FromField!);
                    changes.Add(new(operation.ToField!, null, Convert.ToString(oldValue, System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "SetDefault":
                    if (migrated.Data.TryGetValue(operation.Field!, out var current) && current is not null) break;
                    var defaultField = targetMeta.ByName[operation.Field!];
                    var rawDefault = operation.Value!.Value;
                    var normalizedDefault = defaultField.Type.Normalize(rawDefault);
                    if (!normalizedDefault.Ok)
                        throw new InvalidOperationException($"Dokumen {source.Name}: default untuk '{operation.Field}' tidak valid: {normalizedDefault.Error}");
                    migrated.Data[operation.Field!] = normalizedDefault.Value;
                    changes.Add(new(operation.Field!, null, Convert.ToString(normalizedDefault.Value, System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "ConvertType":
                    if (!migrated.Data.TryGetValue(operation.Field!, out var value) || value is null) break;
                    var field = targetMeta.ByName[operation.Field!];
                    var normalized = field.Type.Normalize(value);
                    if (!normalized.Ok)
                        throw new InvalidOperationException($"Dokumen {source.Name}: nilai '{operation.Field}' tidak dapat dikonversi ke {field.Def.Type}: {normalized.Error}");
                    migrated.Data[operation.Field!] = normalized.Value;
                    changes.Add(new(operation.Field!, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(normalized.Value, System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                default:
                    throw new InvalidOperationException($"Operasi migrasi '{operation.Type}' tidak didukung.");
            }
        }

        migrated.Version++;
        migrated.SchemaVersion = targetMeta.Version;
        migrated.Modified = now;
        migrated.ModifiedBy = actor;
        return (migrated, changes);
    }
}

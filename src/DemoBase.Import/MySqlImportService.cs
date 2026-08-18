using DemoBase.Core.DTOs;
using DemoBase.Core.Interfaces;

namespace DemoBase.Import;

/// <summary>
/// Import MySQL non utilisé — placeholder vide pour satisfaire l'interface IImportService.
/// L'import se fera directement depuis Demozoo via l'API REST ou un dump PostgreSQL.
/// </summary>
public class MySqlImportService : IImportService
{
    public Task<ImportResult> ImportFromMySqlAsync(
        MySqlImportOptions options,
        IProgress<ImportProgress>? progress = null)
        => throw new NotSupportedException("Import MySQL désactivé. Utilisez l'import Demozoo.");

    public Task<ImportResult> ImportFromCsvAsync(
        string csvPath,
        IProgress<ImportProgress>? progress = null)
        => throw new NotSupportedException("Import CSV désactivé. Utilisez l'import Demozoo.");
}

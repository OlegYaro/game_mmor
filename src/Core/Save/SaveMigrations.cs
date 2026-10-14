using System.Text.Json.Nodes;

namespace Core.Save;

/// <summary>
/// One step of the linear migration chain (ADR 0007): turns a save of <see cref="FromVersion"/> into
/// <c>FromVersion + 1</c> by editing the JSON tree, before it is bound to the (current-version-only) DTOs.
/// </summary>
/// <remarks>
/// Rules: a migration only ever edits JSON, never references DTO or simulation types (they change later); it is
/// never edited after release; and it comes with a sample file of <see cref="FromVersion"/> in
/// <c>tests/Core.Tests/Golden/</c> that must keep loading.
/// </remarks>
public interface ISaveMigration
{
    /// <summary>Version this migration reads; it produces <c>FromVersion + 1</c>.</summary>
    int FromVersion { get; }

    /// <summary>Edits the parsed header and body in place. <c>formatVersion</c> in the header is updated by the caller.</summary>
    void Migrate(JsonObject header, JsonObject body);
}

/// <summary>The migrations shipped with the game.</summary>
public static class SaveMigrations
{
    /// <summary>
    /// All shipped migrations. Empty: v1 is the first format version. The chain itself is exercised by tests with a
    /// synthetic older version and a test-only migration.
    /// </summary>
    public static IReadOnlyList<ISaveMigration> All { get; } = [];
}

/// <summary>A missing link in the migration chain.</summary>
internal sealed class MissingMigrationException(string message) : Exception(message);

/// <summary>Runs the migration chain from a file's version up to a target version.</summary>
internal static class SaveMigrator
{
    /// <summary>Header property holding the format version.</summary>
    public const string VersionProperty = "formatVersion";

    /// <summary>
    /// Applies <c>from → from+1 → … → to</c>, setting <c>formatVersion</c> after each step. Nothing happens when
    /// <paramref name="from"/> equals <paramref name="to"/>.
    /// </summary>
    /// <exception cref="MissingMigrationException">A step has no migration, or two migrations claim the same step.</exception>
    public static void Run(JsonObject header, JsonObject body, int from, int to, IReadOnlyList<ISaveMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);

        for (int version = from; version < to; version++)
        {
            ISaveMigration? step = null;
            foreach (ISaveMigration migration in migrations)
            {
                if (migration.FromVersion != version)
                {
                    continue;
                }

                if (step is not null)
                {
                    throw new MissingMigrationException($"Two migrations are registered for format v{version} → v{version + 1}.");
                }

                step = migration;
            }

            if (step is null)
            {
                throw new MissingMigrationException(
                    $"No migration from save format v{version} to v{version + 1}; this build cannot load saves older than v{FirstLoadableVersion(migrations, to)}.");
            }

            step.Migrate(header, body);
            header[VersionProperty] = version + 1;
        }
    }

    // Oldest version from which an unbroken chain reaches `to`.
    private static int FirstLoadableVersion(IReadOnlyList<ISaveMigration> migrations, int to)
    {
        int version = to;
        while (migrations.Any(m => m.FromVersion == version - 1))
        {
            version--;
        }

        return version;
    }
}

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
    /// <summary>All shipped migrations, one per format step (order does not matter; the chain is resolved by version).</summary>
    public static IReadOnlyList<ISaveMigration> All { get; } = [new SaveMigrationV1ToV2(), new SaveMigrationV2ToV3()];
}

/// <summary>
/// v1 → v2 (M1, FR-SAVE-05, FR-WORLD-06). v1 saves hold only the simulation core, so they become games without a
/// world: <c>game = {difficulty: "normal", mapSize: 0, mapHash: 0}</c>, <c>fog = {exploredRuns: []}</c> and an empty
/// <c>components.revealer</c> store. The header is unchanged. Frozen: never edit after release.
/// </summary>
internal sealed class SaveMigrationV1ToV2 : ISaveMigration
{
    /// <inheritdoc />
    public int FromVersion => 1;

    /// <inheritdoc />
    public void Migrate(JsonObject header, JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body["components"] is not JsonObject components)
        {
            throw new InvalidOperationException("v1 body has no 'components' object.");
        }

        if (body.ContainsKey("game") || body.ContainsKey("fog") || components.ContainsKey("revealer"))
        {
            throw new InvalidOperationException("v1 body already has v2 members ('game', 'fog' or 'components.revealer').");
        }

        components["revealer"] = new JsonArray();
        body["game"] = new JsonObject
        {
            ["difficulty"] = "normal",
            ["mapSize"] = 0,
            ["mapHash"] = 0,
        };
        body["fog"] = new JsonObject
        {
            ["exploredRuns"] = new JsonArray(),
        };
    }
}

/// <summary>
/// v2 → v3 (M2, FR-WORLD-05, FR-HERO-01). Adds empty <c>components.hero</c>, <c>components.inventory</c> and
/// <c>components.mover</c> stores and an empty <c>tiles = {resources: [], occupancy: []}</c>: v2 games had no hero and
/// no tile changes. The hero itself needs the content's balance, so it is not created here: on load, the entity with
/// the hero revealer (the start marker of v2) becomes the hero with default data and an empty inventory
/// (<c>Simulation.Restore</c>). The header is unchanged. Frozen: never edit after release.
/// </summary>
internal sealed class SaveMigrationV2ToV3 : ISaveMigration
{
    /// <inheritdoc />
    public int FromVersion => 2;

    /// <inheritdoc />
    public void Migrate(JsonObject header, JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body["components"] is not JsonObject components)
        {
            throw new InvalidOperationException("v2 body has no 'components' object.");
        }

        if (body.ContainsKey("tiles") || components.ContainsKey("hero") || components.ContainsKey("inventory") || components.ContainsKey("mover"))
        {
            throw new InvalidOperationException("v2 body already has v3 members ('tiles' or 'components.hero', 'inventory', 'mover').");
        }

        components["hero"] = new JsonArray();
        components["inventory"] = new JsonArray();
        components["mover"] = new JsonArray();
        body["tiles"] = new JsonObject
        {
            ["resources"] = new JsonArray(),
            ["occupancy"] = new JsonArray(),
        };
    }
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

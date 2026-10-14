using System.Reflection;

namespace Core.Tests;

/// <summary>
/// Guards the layer boundary from ADR 0002: Core is pure C# and never depends on the engine.
/// </summary>
public sealed class ArchitectureTests
{
    // NFR-04 (determinism, replay in CI) and NFR-06 (Core coverage) both rely on Core running without Godot.
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "NFR-06")]
    public void CoreReferencesNoGodotAssemblies()
    {
        AssemblyName[] references = typeof(CoreInfo).Assembly.GetReferencedAssemblies();

        Assert.NotEmpty(references);
        Assert.DoesNotContain(
            references,
            reference => reference.Name is not null && reference.Name.StartsWith("Godot", StringComparison.OrdinalIgnoreCase));
    }
}

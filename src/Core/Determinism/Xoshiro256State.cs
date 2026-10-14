namespace Core.Determinism;

/// <summary>
/// Snapshot of a <see cref="Xoshiro256StarStar"/> state (the reference <c>s[0..3]</c>). Saved and restored as is.
/// The all-zero state is invalid: the generator would only ever return zero.
/// </summary>
public readonly record struct Xoshiro256State(ulong S0, ulong S1, ulong S2, ulong S3)
{
    /// <summary>True for the all-zero state, which a generator must never use.</summary>
    public bool IsZero => (S0 | S1 | S2 | S3) == 0;
}

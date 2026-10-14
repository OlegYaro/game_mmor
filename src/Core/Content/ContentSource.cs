namespace Core.Content;

/// <summary>One content file: a forward-slash path relative to the data root (e.g. <c>items/basic.json</c>) and its text.</summary>
public sealed record ContentFile(string Path, string Text);

/// <summary>Provides raw content files. Core never touches the filesystem; Game and tools implement this.</summary>
public interface IContentSource
{
    /// <summary>Returns all content files (not schemas).</summary>
    IReadOnlyList<ContentFile> GetFiles();
}

/// <summary>In-memory source, mainly for tests.</summary>
public sealed class InMemoryContentSource : IContentSource
{
    private readonly List<ContentFile> _files = [];

    /// <summary>Adds a file and returns this source.</summary>
    public InMemoryContentSource Add(string path, string text)
    {
        _files.Add(new ContentFile(path, text));
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentFile> GetFiles() => _files;
}

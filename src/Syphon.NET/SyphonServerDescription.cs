namespace Syphon.NET;

/// <summary>
/// Identifies a Syphon server discovered through the <see cref="SyphonServerDirectory"/>.
/// </summary>
/// <param name="Uuid">Stable unique identifier for the server instance.</param>
/// <param name="AppName">Name of the application hosting the server.</param>
/// <param name="Name">Server-specific name, which may be empty for an unnamed server.</param>
public readonly record struct SyphonServerDescription(string Uuid, string AppName, string Name);

namespace Syphon.NET;

/// <summary>A Syphon operation failed: a server, a client, or their messaging.</summary>
public sealed class SyphonException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SyphonException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    public SyphonException(string message)
        : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The failure behind it.</param>
    public SyphonException(string message, Exception innerException)
        : base(message, innerException) { }
}

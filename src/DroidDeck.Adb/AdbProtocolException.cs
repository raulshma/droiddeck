namespace DroidDeck.Adb;

/// <summary>
/// Raised when the adb server returns a protocol error or closes the connection unexpectedly.
/// </summary>
public sealed class AdbProtocolException : Exception
{
    /// <summary>Creates an exception.</summary>
    public AdbProtocolException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public AdbProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and inner exception.</summary>
    public AdbProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

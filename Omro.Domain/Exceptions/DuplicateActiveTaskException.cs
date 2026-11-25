namespace Omro.Domain.Exceptions;

public sealed class DuplicateActiveTaskException : Exception
{
    public DuplicateActiveTaskException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

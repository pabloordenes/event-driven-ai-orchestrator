namespace Omro.Infrastructure.Security;

public sealed class SsrfBlockedException : Exception
{
    public SsrfBlockedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

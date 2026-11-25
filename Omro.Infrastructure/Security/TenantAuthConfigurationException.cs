namespace Omro.Infrastructure.Security;

public sealed class TenantAuthConfigurationException : Exception
{
    public TenantAuthConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

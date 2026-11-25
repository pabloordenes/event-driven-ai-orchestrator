namespace Omro.Application.Common.Exceptions;

public class TransientWebhookDispatchException : Exception
{
    public int? HttpStatusCode { get; }

    public TransientWebhookDispatchException(string message, int? httpStatusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        HttpStatusCode = httpStatusCode;
    }
}

public class PermanentWebhookException : Exception
{
    public int? HttpStatusCode { get; }

    public PermanentWebhookException(string message, int? httpStatusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        HttpStatusCode = httpStatusCode;
    }
}

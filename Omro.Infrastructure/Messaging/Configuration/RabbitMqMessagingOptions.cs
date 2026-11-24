namespace Omro.Infrastructure.Messaging.Configuration;

public class RabbitMqMessagingOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string? VirtualHost { get; set; }
    public bool UseSsl { get; set; } = false;

    /// <summary>
    /// Modos soportados: "QuartzInMemory" (default), "RabbitMqDelayedExchange", "None".
    /// </summary>
    public string RedeliveryMode { get; set; } = "QuartzInMemory";

    public int RetryLimit { get; set; } = 3;
    public int RetryMinIntervalMs { get; set; } = 2000;
    public int RetryMaxIntervalMs { get; set; } = 20000;
    public int RetryIntervalDeltaMs { get; set; } = 4000;
    public int[] RedeliveryIntervalsMs { get; set; } = [60_000, 300_000, 900_000];
}

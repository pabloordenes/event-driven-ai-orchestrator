using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Security;
using Polly;
using Polly.Timeout;

namespace Omro.Infrastructure.HttpClients;

public class WebhookResilienceOptions
{
    public int AttemptTimeoutSeconds { get; set; } = 15;
    public int TotalRequestTimeoutSeconds { get; set; } = 60;
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
    public bool UseJitter { get; set; } = true;
    public int SamplingDurationSeconds { get; set; } = 45;
    public int MinimumThroughput { get; set; } = 5;
    public double FailureRatio { get; set; } = 0.5;
    public int BreakDurationSeconds { get; set; } = 30;
}

public static class WebhookResilienceExtensions
{
    public static IHttpClientBuilder AddResilientWebhookHttpClient(
        this IServiceCollection services,
        Action<WebhookResilienceOptions>? configure = null,
        Action<IHttpClientBuilder>? configureClientBuilder = null)
    {
        var resilienceOptions = new WebhookResilienceOptions();
        configure?.Invoke(resilienceOptions);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton<ISsrfGuard, SsrfGuard>();
        services.TryAddSingleton<ISecretResolver, ConfigurationSecretResolver>();
        services.TryAddSingleton<ITenantAuthSigner, TenantAuthSigner>();
        services.TryAddTransient<WebhookAttemptCounterHandler>();

        var clientBuilder = services.AddHttpClient<IWebhookDispatcher, WebhookDispatcher>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        clientBuilder.ConfigurePrimaryHttpMessageHandler(sp =>
            CreateSsrfProtectedSocketsHandler(sp.GetRequiredService<ISsrfGuard>()));

        configureClientBuilder?.Invoke(clientBuilder);

        clientBuilder
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(1, resilienceOptions.AttemptTimeoutSeconds));
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(
                    resilienceOptions.AttemptTimeoutSeconds,
                    resilienceOptions.TotalRequestTimeoutSeconds));

                options.Retry.MaxRetryAttempts = Math.Max(1, resilienceOptions.MaxRetryAttempts);
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = resilienceOptions.UseJitter;
                options.Retry.Delay = TimeSpan.FromMilliseconds(Math.Max(1, resilienceOptions.RetryDelayMs));
                options.Retry.ShouldHandle = args => ValueTask.FromResult(IsTransientOutcome(args.Outcome));

                var minSamplingSeconds = Math.Max(
                    resilienceOptions.SamplingDurationSeconds,
                    resilienceOptions.AttemptTimeoutSeconds * 2);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(minSamplingSeconds);
                options.CircuitBreaker.MinimumThroughput = Math.Max(2, resilienceOptions.MinimumThroughput);
                options.CircuitBreaker.FailureRatio = resilienceOptions.FailureRatio;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(Math.Max(1, resilienceOptions.BreakDurationSeconds));
                options.CircuitBreaker.ShouldHandle = args => ValueTask.FromResult(IsTransientOutcome(args.Outcome));
            })
            .SelectPipelineByAuthority();

        clientBuilder.AddHttpMessageHandler<WebhookAttemptCounterHandler>();

        return clientBuilder;
    }

    public static SocketsHttpHandler CreateSsrfProtectedSocketsHandler(ISsrfGuard ssrfGuard)
    {
        ArgumentNullException.ThrowIfNull(ssrfGuard);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var validation = await ssrfGuard.ValidateHostAsync(context.DnsEndPoint.Host, cancellationToken);
                if (!validation.IsAllowed || validation.ResolvedAddresses.Length == 0)
                {
                    throw new SsrfBlockedException(
                        validation.BlockReason ?? $"Host '{context.DnsEndPoint.Host}' bloqueado por guardia SSRF.");
                }

                Exception? lastError = null;
                foreach (var address in validation.ResolvedAddresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                            throw;

                        lastError = ex;
                    }
                }

                throw new HttpRequestException(
                    $"No se pudo conectar a '{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}' en ninguna IP pública resuelta.",
                    lastError);
            }
        };
    }

    public static bool IsTransientOutcome(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is not null)
        {
            if (IsSsrfException(outcome.Exception))
                return false;

            return outcome.Exception is HttpRequestException
                or TimeoutRejectedException
                or TaskCanceledException;
        }

        if (outcome.Result is null)
            return false;

        return IsTransientHttpStatus(outcome.Result.StatusCode);
    }

    public static bool IsTransientHttpStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500 || statusCode == HttpStatusCode.RequestTimeout || statusCode == HttpStatusCode.TooManyRequests;
    }

    public static bool IsPermanentHttpStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 300 and < 500 && !IsTransientHttpStatus(statusCode);
    }

    private static bool IsSsrfException(Exception ex) =>
        ex is SsrfBlockedException ||
        ex.InnerException is SsrfBlockedException ||
        ex.GetBaseException() is SsrfBlockedException;
}

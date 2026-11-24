using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Omro.Application.Interfaces;
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

        services.TryAddTransient<WebhookAttemptCounterHandler>();

        var clientBuilder = services.AddHttpClient<IWebhookDispatcher, WebhookDispatcher>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

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

    public static bool IsTransientOutcome(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is not null)
        {
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
        return code is >= 400 and < 500 && !IsTransientHttpStatus(statusCode);
    }
}

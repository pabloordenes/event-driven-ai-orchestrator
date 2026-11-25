using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Contracts.Events;
using Omro.Domain.Messaging;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Infrastructure.HttpClients;
using Omro.Infrastructure.Messaging.Configuration;
using Omro.Infrastructure.Messaging.Observers;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Worker.Consumers;

namespace Omro.IntegrationTests;

public class WebhookDispatcherResilienceTests
{
    [Fact]
    public async Task DispatchAsync_When503TwiceThen200_ShouldRetryAndReturnSucceededWith3Attempts()
    {
        int wireHits = 0;
        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            var hit = Interlocked.Increment(ref wireHits);
            return hit < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        await using var sp = BuildDispatcherProvider(handler, opt =>
        {
            opt.MaxRetryAttempts = 3;
            opt.RetryDelayMs = 10;
            opt.UseJitter = false;
            opt.AttemptTimeoutSeconds = 2;
            opt.SamplingDurationSeconds = 10;
            opt.BreakDurationSeconds = 5;
        });

        var dispatcher = sp.GetRequiredService<IWebhookDispatcher>();
        var taskId = Guid.NewGuid();

        var result = await dispatcher.DispatchAsync(
            "https://tenant-retry.test/api/webhook",
            new Dictionary<string, object> { ["ping"] = true },
            CancellationToken.None,
            "idem-retry-1",
            taskId);

        result.IsSuccess.Should().BeTrue();
        result.IsPermanentFailure.Should().BeFalse();
        result.HttpStatusCode.Should().Be(200);
        result.Attempts.Should().Be(3);
        wireHits.Should().Be(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task DispatchAsync_WhenPermanent4xx_ShouldNotRetryAndReportPermanentFailure(HttpStatusCode statusCode)
    {
        int wireHits = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref wireHits);
            return new HttpResponseMessage(statusCode) { Content = new StringContent("bad payload") };
        });

        await using var sp = BuildDispatcherProvider(handler, opt =>
        {
            opt.MaxRetryAttempts = 3;
            opt.RetryDelayMs = 10;
            opt.AttemptTimeoutSeconds = 2;
            opt.SamplingDurationSeconds = 10;
            opt.BreakDurationSeconds = 5;
        });

        var dispatcher = sp.GetRequiredService<IWebhookDispatcher>();

        var result = await dispatcher.DispatchAsync(
            "https://tenant-4xx.test/api/webhook",
            new Dictionary<string, object> { ["ok"] = false },
            CancellationToken.None,
            "idem-4xx");

        result.IsSuccess.Should().BeFalse();
        result.IsPermanentFailure.Should().BeTrue();
        result.HttpStatusCode.Should().Be((int)statusCode);
        result.Attempts.Should().Be(1);
        wireHits.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WhenCircuitBreakerOpensForHostA_HostBContinuesWorkingIndependently()
    {
        int hostAHits = 0;
        int hostBHits = 0;

        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.Authority == "host-a.tenant.test")
            {
                Interlocked.Increment(ref hostAHits);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Interlocked.Increment(ref hostBHits);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using var sp = BuildDispatcherProvider(handler, opt =>
        {
            opt.MaxRetryAttempts = 1;
            opt.RetryDelayMs = 1;
            opt.UseJitter = false;
            opt.MinimumThroughput = 6;
            opt.FailureRatio = 0.5;
            opt.AttemptTimeoutSeconds = 2;
            opt.SamplingDurationSeconds = 10;
            opt.BreakDurationSeconds = 30;
        });

        var dispatcher = sp.GetRequiredService<IWebhookDispatcher>();

        for (int i = 0; i < 3; i++)
        {
            var failResult = await dispatcher.DispatchAsync(
                "https://host-a.tenant.test/api/hook",
                new Dictionary<string, object> { ["i"] = i },
                CancellationToken.None,
                $"host-a-{i}");
            failResult.IsSuccess.Should().BeFalse();
            failResult.IsPermanentFailure.Should().BeFalse();
        }

        hostAHits.Should().Be(6);

        // 4a llamada al host A debe ser bloqueada por el circuit breaker sin tocar la red
        var openResult = await dispatcher.DispatchAsync(
            "https://host-a.tenant.test/api/hook",
            new Dictionary<string, object> { ["i"] = 4 },
            CancellationToken.None,
            "host-a-4");

        openResult.IsSuccess.Should().BeFalse();
        openResult.IsPermanentFailure.Should().BeFalse();
        openResult.Attempts.Should().Be(0);
        openResult.ErrorMessage.Should().Contain("Circuit breaker abierto");
        hostAHits.Should().Be(6);

        // Host B debe operar normalmente sin verse afectado por el circuito abierto de Host A
        var hostBResult = await dispatcher.DispatchAsync(
            "https://host-b.tenant.test/api/hook",
            new Dictionary<string, object> { ["ok"] = true },
            CancellationToken.None,
            "host-b-1");

        hostBResult.IsSuccess.Should().BeTrue();
        hostBResult.HttpStatusCode.Should().Be(200);
        hostBResult.Attempts.Should().Be(1);
        hostBHits.Should().Be(1);
    }

    [Fact]
    public async Task Consumers_WhenSameMessageIdDeliveredTwice_ShouldDeduplicateViaInbox()
    {
        int orchestratorCalls = 0;
        int webhookCalls = 0;

        var services = new ServiceCollection();
        var dbName = $"OmroInboxDb_{Guid.NewGuid()}";
        var inMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();

        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseInMemoryDatabase(dbName).UseInternalServiceProvider(inMemoryProvider));
        services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
        services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
        services.AddScoped<IActionExecutionCoordinator, ActionExecutionCoordinator>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IMessageInboxStore, EfMessageInboxStore>();

        var webhookMock = new Moq.Mock<IWebhookDispatcher>();
        webhookMock
            .Setup(w => w.DispatchAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Dictionary<string, object>>(),
                Moq.It.IsAny<CancellationToken>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<Guid?>()))
            .Callback(() => Interlocked.Increment(ref webhookCalls))
            .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));
        services.AddSingleton(webhookMock.Object);

        var orchestratorMock = new Moq.Mock<ICognitiveOrchestrator>();
        orchestratorMock
            .Setup(o => o.ProposeResolutionAsync(
                Moq.It.IsAny<AnomalyEvent>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref orchestratorCalls))
            .ReturnsAsync((
                new CognitivePlan(
                    "Plan",
                    "Reason",
                    0.9m,
                    [new ProposedAction("OVERBOOK", "https://medcare.test/api/appointments/overbook", new Dictionary<string, object> { ["ok"] = true })]),
                10,
                20));
        services.AddSingleton(orchestratorMock.Object);

        services.Configure<RabbitMqMessagingOptions>(o =>
        {
            o.RedeliveryMode = "None";
            o.RetryLimit = 1;
            o.RetryMinIntervalMs = 15;
            o.RetryMaxIntervalMs = 30;
            o.RetryIntervalDeltaMs = 10;
        });

        var receiveObserver = new CountingReceiveObserver();
        services.AddSingleton<IOperationalAlertSink, LoggingOperationalAlertSink>();
        services.AddSingleton<DeadLetterAlertObserver>();
        services.AddSingleton(receiveObserver);

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommandHandler>());
        services.AddMassTransitTestHarness(x =>
        {
            x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(15), testInactivityTimeout: TimeSpan.FromSeconds(2));
            x.SetKebabCaseEndpointNameFormatter();
            x.AddConsumer<AnomalyEventConsumer, AnomalyEventConsumerDefinition>();
            x.AddConsumer<ResolutionApprovedConsumer, ResolutionApprovedConsumerDefinition>();
            x.UsingInMemory((context, cfg) =>
            {
                cfg.ConnectReceiveObserver(context.GetRequiredService<DeadLetterAlertObserver>());
                cfg.ConnectReceiveObserver(context.GetRequiredService<CountingReceiveObserver>());
                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddLogging();

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var anomalyMsgId = Guid.NewGuid();
            var anomaly = new AnomalyDetected(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "AppointmentNoShowRisk",
                Guid.NewGuid(),
                0.91m,
                new Dictionary<string, string>());

            await harness.Bus.Publish(anomaly, ctx => ctx.MessageId = anomalyMsgId);
            await harness.Bus.Publish(anomaly, ctx => ctx.MessageId = anomalyMsgId);

            var approvedMsgId = Guid.NewGuid();
            var approved = new ResolutionApproved(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "{\"DeferredActions\":[{\"Endpoint\":\"https://medcare.test/api/appointments/monitor\",\"Payload\":{\"ok\":true}}]}");

            await harness.Bus.Publish(approved, ctx => ctx.MessageId = approvedMsgId);
            await harness.Bus.Publish(approved, ctx => ctx.MessageId = approvedMsgId);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (receiveObserver.GetPostReceiveCount(anomalyMsgId) >= 2 &&
                    receiveObserver.GetPostReceiveCount(approvedMsgId) >= 2)
                {
                    break;
                }
                await Task.Delay(30);
            }

            receiveObserver.GetPostReceiveCount(anomalyMsgId).Should().Be(2);
            receiveObserver.GetPostReceiveCount(approvedMsgId).Should().Be(2);
            orchestratorCalls.Should().Be(1);
            webhookCalls.Should().Be(2); // 1 de AnomalyDetected + 1 de ResolutionApproved

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var inboxRows = await db.ProcessedMessages.AsNoTracking().ToListAsync();
            inboxRows.Should().HaveCount(2);
            inboxRows.Should().OnlyContain(r => r.Status == ProcessedMessage.StatusCompleted);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static ServiceProvider BuildDispatcherProvider(
        HttpMessageHandler primaryHandler,
        Action<WebhookResilienceOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientWebhookHttpClient(
            configure,
            builder => builder.ConfigurePrimaryHttpMessageHandler(() => primaryHandler));
        return services.BuildServiceProvider();
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _fn;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> fn)
            => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request, cancellationToken));
    }

    private sealed class CountingReceiveObserver : IReceiveObserver
    {
        private readonly ConcurrentDictionary<Guid, int> _counts = new();

        public int GetPostReceiveCount(Guid messageId) =>
            _counts.TryGetValue(messageId, out var c) ? c : 0;

        public Task PreReceive(ReceiveContext context) => Task.CompletedTask;

        public Task PostReceive(ReceiveContext context)
        {
            var id = context.GetMessageId();
            if (id.HasValue)
                _counts.AddOrUpdate(id.Value, 1, (_, c) => c + 1);
            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class
            => Task.CompletedTask;

        public Task ConsumeFault<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception) where T : class
            => Task.CompletedTask;

        public Task ReceiveFault(ReceiveContext context, Exception exception) => Task.CompletedTask;
    }
}

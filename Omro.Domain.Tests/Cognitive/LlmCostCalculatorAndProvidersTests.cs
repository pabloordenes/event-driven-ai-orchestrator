using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Domain.ResolutionTasks;
using Omro.Infrastructure.CognitiveServices;
using Omro.Infrastructure.CognitiveServices.Providers;
using Omro.Infrastructure.Configuration;

namespace Omro.Domain.Tests.Cognitive;

public class LlmCostCalculatorAndProvidersTests
{
    [Fact]
    public void LlmCostCalculator_ShouldCalculateBuiltInConfiguredAndFallbackRatesAccurately()
    {
        var defaultCalc = new LlmCostCalculator();

        // gemini-2.5-flash: $0.15 / 1M prompt + $0.60 / 1M completion
        var flashCost = defaultCalc.CalculateEstimatedCostUsd("Gemini", "gemini-2.5-flash", 2000, 1000);
        flashCost.Should().Be(0.00090000m); // 0.0003 + 0.0006

        // gemini-2.5-pro: $1.25 / 1M prompt + $10.00 / 1M completion
        var proCost = defaultCalc.CalculateEstimatedCostUsd("Gemini", "gemini-2.5-pro", 1_000_000, 500_000);
        proCost.Should().Be(6.25m);

        // Fallback rate ($0.50 / $1.50)
        var fallbackCost = defaultCalc.CalculateEstimatedCostUsd("Other", "unknown-model-v9", 1_000_000, 1_000_000);
        fallbackCost.Should().Be(2.00m);

        // Configured override
        var customCalc = new LlmCostCalculator(Options.Create(new CognitiveOrchestratorOptions
        {
            PricingPerMillionTokens = new Dictionary<string, ModelPricingOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai/gpt-oss-120b"] = new() { InputUsdPerMillion = 1.00m, OutputUsdPerMillion = 2.00m }
            }
        }));
        customCalc.CalculateEstimatedCostUsd("Groq", "openai/gpt-oss-120b", 500_000, 250_000).Should().Be(1.00m);
    }

    [Fact]
    public async Task GroqLlmProvider_ShouldStripMarkdownFencesExtractUsageAndPropagateHttpStatus()
    {
        HttpRequestMessage? capturedReq = null;
        string? capturedAuth = null;

        var successClient = new HttpClient(new DelegateHttpHandler((req, _) =>
        {
            capturedReq = req;
            capturedAuth = req.Headers.Authorization?.ToString();
            var json = """
            {
              "choices": [
                {
                  "message": {
                    "content": "```json\n{\"Title\":\"Groq Plan\",\"Reasoning\":\"OK\",\"ConfidenceScore\":0.91,\"ExpectsExternalApproval\":false,\"ProposedActions\":[]}\n```"
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 120,
                "completion_tokens": 45
              }
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }))
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        var provider = GroqLlmProvider.CreateWithHttpClient(
            successClient,
            Options.Create(new GroqOptions { ApiKey = "groq-test-key", Model = "openai/gpt-oss-120b" }),
            NullLogger<GroqLlmProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();

        var result = await provider.ProposeResolutionAsync(new LlmProviderRequest(
            new AnomalyEvent(0.90m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Tenant context",
            new Dictionary<string, string> { ["priority"] = "high" }));

        capturedReq!.RequestUri!.PathAndQuery.Should().Be("/openai/v1/chat/completions");
        capturedAuth.Should().Be("Bearer groq-test-key");
        result.ProviderName.Should().Be("Groq");
        result.ModelName.Should().Be("openai/gpt-oss-120b");
        result.PromptTokens.Should().Be(120);
        result.CompletionTokens.Should().Be(45);
        result.Plan.Title.Should().Be("Groq Plan");

        var errorClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limit") })))
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        var errorProvider = GroqLlmProvider.CreateWithHttpClient(
            errorClient,
            Options.Create(new GroqOptions { ApiKey = "groq-test-key" }),
            NullLogger<GroqLlmProvider>.Instance);

        Func<Task> act = () => errorProvider.ProposeResolutionAsync(new LlmProviderRequest(
            new AnomalyEvent(0.90m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Context",
            null));

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GeminiLlmProvider_ShouldUseHeaderApiKeyParseUsageMetadataAndPropagateHttpStatus()
    {
        HttpRequestMessage? capturedReq = null;
        string? capturedBody = null;
        string? capturedHeaderKey = null;

        var successClient = new HttpClient(new DelegateHttpHandler(async (req, ct) =>
        {
            capturedReq = req;
            capturedHeaderKey = req.Headers.TryGetValues("x-goog-api-key", out var vals) ? vals.FirstOrDefault() : null;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);

            var responseJson = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "text": "{\"Title\":\"Gemini Plan\",\"Reasoning\":\"Contextualizado por Gemini\",\"ConfidenceScore\":0.94,\"ExpectsExternalApproval\":false,\"ProposedActions\":[]}"
                      }
                    ]
                  }
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 210,
                "candidatesTokenCount": 65
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        }))
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/")
        };

        var provider = GeminiLlmProvider.CreateWithHttpClient(
            successClient,
            Options.Create(new GeminiOptions { ApiKey = "gemini-secret-key", Model = "gemini-2.5-flash" }),
            new ConfigurationBuilder().Build(),
            NullLogger<GeminiLlmProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();

        var result = await provider.ProposeResolutionAsync(new LlmProviderRequest(
            new AnomalyEvent(0.88m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Tenant context",
            null));

        capturedReq!.RequestUri!.PathAndQuery.Should().Be("/v1beta/models/gemini-2.5-flash:generateContent");
        capturedReq.RequestUri.Query.Should().BeNullOrEmpty("la API key no debe viajar en query string");
        capturedHeaderKey.Should().Be("gemini-secret-key");

        using var reqDoc = JsonDocument.Parse(capturedBody!);
        reqDoc.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString()
            .Should().Be("application/json");
        reqDoc.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()
            .Should().Contain("OMRO");

        result.ProviderName.Should().Be("Gemini");
        result.ModelName.Should().Be("gemini-2.5-flash");
        result.PromptTokens.Should().Be(210);
        result.CompletionTokens.Should().Be(65);
        result.Plan.Title.Should().Be("Gemini Plan");

        var errorClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("bad gateway") })))
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/")
        };

        var errorProvider = GeminiLlmProvider.CreateWithHttpClient(
            errorClient,
            Options.Create(new GeminiOptions { ApiKey = "gemini-secret-key" }),
            null,
            NullLogger<GeminiLlmProvider>.Instance);

        Func<Task> act = () => errorProvider.ProposeResolutionAsync(new LlmProviderRequest(
            new AnomalyEvent(0.88m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Context",
            null));

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    private sealed class DelegateHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _fn(request, cancellationToken);
    }
}

using OpenTelemetry.Trace;

namespace CIWorkbench.Web;

/// <summary>
/// Blazor Server dispatches every UI event, render acknowledgement and JS call through SignalR hub methods, and the ASP.NET Core
/// instrumentation turns each one into a trace. They carry no diagnostic value here and drown the GitHub spans in the dashboard.
/// Only those hub spans are dropped; everything else (HTTP requests, component events, GitHub requests) is kept.
/// </summary>
public sealed class BlazorHubNoiseSampler : Sampler
{
    private const string HubSpanPrefix = "Microsoft.AspNetCore.Components.Server.ComponentHub/";
    private static readonly SamplingResult Drop = new(SamplingDecision.Drop);
    private static readonly SamplingResult Keep = new(SamplingDecision.RecordAndSample);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Name.StartsWith(HubSpanPrefix, StringComparison.Ordinal) ? Drop : Keep;
}
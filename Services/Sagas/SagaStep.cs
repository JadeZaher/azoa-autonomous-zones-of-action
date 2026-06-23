using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// Concrete, strongly-typed step. Closes <see cref="IStepHandler{TPayload}"/>
/// over <typeparamref name="TPayload"/> and JSON-deserializes the persisted
/// payload at dispatch time. The handler is resolved from the per-tick DI scope
/// the processor passes in (same scope-per-tick discipline as
/// <c>ReconciliationHostedService</c>), so handlers can depend on scoped
/// services (<c>IIdempotencyStore</c>, the per-aggregate stores, …).
/// </summary>
public sealed class SagaStep<TPayload> : ISagaStep, IStepDispatch
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public SagaStep(
        string name,
        RetryPolicy? retryPolicy = null,
        string? compensationStepName = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Step name must be non-empty.", nameof(name));
        Name = name;
        RetryPolicy = retryPolicy ?? RetryPolicy.Default;
        CompensationStepName = compensationStepName;
    }

    public string Name { get; }
    public RetryPolicy RetryPolicy { get; }
    public string? CompensationStepName { get; }
    public IStepDispatch Dispatch => this;

    public async Task<StepResult> DispatchAsync(
        string sagaName,
        string stepName,
        string correlationKey,
        string stepIdempotencyKey,
        int attempt,
        string payloadJson,
        IServiceProvider scope,
        CancellationToken ct)
    {
        TPayload payload;
        try
        {
            payload = string.IsNullOrEmpty(payloadJson)
                ? default!
                : JsonSerializer.Deserialize<TPayload>(payloadJson, JsonOptions)!;
        }
        catch (JsonException ex)
        {
            // A corrupt/unsupported payload is a non-retryable defect — surface
            // it as a failed attempt so the normal retry→compensation→dead-letter
            // machinery records it rather than throwing out of the processor.
            return StepResult.Fail($"Payload deserialization failed for step '{stepName}': {ex.Message}");
        }

        var handler = scope.GetRequiredService<IStepHandler<TPayload>>();
        var ctx = new StepExecutionContext<TPayload>(
            sagaName, stepName, correlationKey, stepIdempotencyKey, attempt, payload);
        return await handler.ExecuteAsync(ctx, ct);
    }

    /// <summary>Serialize a payload for persistence into the outbox record.</summary>
    public static string Serialize(TPayload payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);
}

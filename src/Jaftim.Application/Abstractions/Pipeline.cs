namespace Jaftim.Application.Abstractions;

/// <summary>The rest of the pipeline after the current step. A step that does not call it short-circuits the run.</summary>
public delegate Task PipelineStepDelegate(CancellationToken ct);

/// <summary>
/// One stage of a pipeline (notification delivery, email delivery, background-job execution). Steps run in their DI
/// registration order; each does its work and calls <c>next</c>, or returns without calling it to stop the run.
/// </summary>
public interface IPipelineStep<in TContext>
{
    Task InvokeAsync(TContext context, PipelineStepDelegate next, CancellationToken ct);
}

/// <summary>
/// Composes every registered <see cref="IPipelineStep{TContext}"/> for one context type, in registration order, around
/// an optional terminal action. The single pipeline mechanism behind messaging and jobs - see docs/ARCHITECTURE.md.
/// </summary>
public sealed class Pipeline<TContext>(IEnumerable<IPipelineStep<TContext>> steps)
{
    private readonly IPipelineStep<TContext>[] _steps = steps.ToArray();

    public Task RunAsync(TContext context, CancellationToken ct) =>
        RunAsync(context, static _ => Task.CompletedTask, ct);

    public Task RunAsync(TContext context, PipelineStepDelegate terminal, CancellationToken ct)
    {
        PipelineStepDelegate next = terminal;
        for (int i = _steps.Length - 1; i >= 0; i--)
        {
            IPipelineStep<TContext> step = _steps[i];
            PipelineStepDelegate inner = next;
            next = c => step.InvokeAsync(context, inner, c);
        }
        return next(ct);
    }
}

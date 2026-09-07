using Dsf.Runtime;

// Dsf.AgentHost is a second, optional executable entrypoint into the exact same
// CLI grammar Dsf.Runtime's own dsf-runtime executable exposes (`serve-agent
// --kind <kind>` in particular): a served source agent can be deployed as this
// dedicated, reusable host instead of the orchestrator's runtime image, without
// duplicating any wiring.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

return await RuntimeCliApplication.InvokeAsync(args, cts.Token);

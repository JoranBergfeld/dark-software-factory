using Dsf.AgentHost;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

return await AgentHostApplication.InvokeAsync(args, cts.Token);

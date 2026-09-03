using Dsf.Runtime;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

return await RuntimeCliApplication.InvokeAsync(args, cts.Token);

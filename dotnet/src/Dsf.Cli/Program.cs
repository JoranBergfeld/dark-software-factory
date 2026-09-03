using Dsf.Cli;

return await EntryPoint.RunAsync(args, cancel =>
{
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancel();
    };
});

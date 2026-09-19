namespace Dsf.Core.Runtime;

public static class NativeCosmosDocumentLimits
{
    public const int MaximumCheckpointBytes = 2 * 1024 * 1024 - 16 * 1024;
}

using System.Threading;

namespace MultiHostPz.App;

public enum UiOperation
{
    Idle,
    Local,
    Cloud
}

public sealed record UiActionAvailability(
    bool CreateSnapshot,
    bool RestoreSnapshot,
    bool SelectCloudProvider,
    bool ConnectCloud,
    bool UseConnectedCloudAction);

public static class UiActionAvailabilityCalculator
{
    public static UiActionAvailability Calculate(
        bool cloudConfigured,
        bool cloudConnected,
        UiOperation operation)
    {
        var idle = operation == UiOperation.Idle;
        return new(
            idle,
            idle,
            idle,
            idle && cloudConfigured && !cloudConnected,
            idle && cloudConfigured && cloudConnected);
    }
}

public sealed class UiOperationGate
{
    private int _operation;

    public UiOperation Current => (UiOperation)Volatile.Read(ref _operation);

    public bool TryBegin(UiOperation operation)
    {
        if (operation == UiOperation.Idle)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return Interlocked.CompareExchange(ref _operation, (int)operation, (int)UiOperation.Idle)
            == (int)UiOperation.Idle;
    }

    public void End(UiOperation operation) =>
        Interlocked.CompareExchange(ref _operation, (int)UiOperation.Idle, (int)operation);
}

namespace MultiHostPz.App.Tests;

public sealed class UiOperationStateTests
{
    [Theory]
    [InlineData(false, false, true, true, false, false)]
    [InlineData(true, false, true, true, true, false)]
    [InlineData(true, true, true, true, false, true)]
    public void IdleAvailability_UsesCloudConfigurationAndConnection(
        bool configured,
        bool connected,
        bool create,
        bool restore,
        bool connect,
        bool connectedActions)
    {
        var result = UiActionAvailabilityCalculator.Calculate(configured, connected, UiOperation.Idle);

        Assert.Equal(new(create, restore, true, connect, connectedActions), result);
    }

    [Theory]
    [InlineData(UiOperation.Local)]
    [InlineData(UiOperation.Cloud)]
    public void BusyAvailability_DisablesLocalAndCloudMutationActions(UiOperation operation)
    {
        var result = UiActionAvailabilityCalculator.Calculate(true, true, operation);

        Assert.Equal(new(false, false, false, false, false), result);
    }

    [Fact]
    public void Gate_PreventsLocalAndCloudReentryUntilMatchingOperationEnds()
    {
        var gate = new UiOperationGate();

        Assert.True(gate.TryBegin(UiOperation.Local));
        Assert.False(gate.TryBegin(UiOperation.Local));
        Assert.False(gate.TryBegin(UiOperation.Cloud));
        gate.End(UiOperation.Cloud);
        Assert.Equal(UiOperation.Local, gate.Current);
        gate.End(UiOperation.Local);
        Assert.True(gate.TryBegin(UiOperation.Cloud));
    }
}

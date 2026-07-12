using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Ray.BiliBiliTool.Web.Services;

public sealed class PortableUiCircuitHandler(PortableUiLifecycleCoordinator lifecycle)
    : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lifecycle.PageConnected(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lifecycle.PageConnected(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lifecycle.PageDisconnected(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lifecycle.PageDisconnected(circuit.Id);
        return Task.CompletedTask;
    }
}

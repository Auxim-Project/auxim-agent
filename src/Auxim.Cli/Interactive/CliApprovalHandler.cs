using Auxim.Core.Approval;

namespace Auxim.Cli.Interactive;

internal sealed class CliApprovalHandler(InteractiveView? view = null) : IApprovalHandler
{
    public async Task<ApprovalResponse> RequestAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.IsInputRedirected) return ApprovalResponse.Deny("CLI approval requires an interactive terminal.");
        try
        {
            var decision = await ApprovalRenderer.PromptAsync(request, cancellationToken, view);
            // The user can interrupt while terminal modes are being restored.
            cancellationToken.ThrowIfCancellationRequested();
            return decision;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return ApprovalResponse.Deny("Safe terminal input failed; approval was denied: " + error.Message); }
    }
}

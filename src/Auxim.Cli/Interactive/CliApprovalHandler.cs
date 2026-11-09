using Auxim.Core.Approval;

namespace Auxim.Cli.Interactive;

internal sealed class CliApprovalHandler : IApprovalHandler
{
    public async Task<ApprovalResponse> RequestAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.IsInputRedirected) return ApprovalResponse.Deny("CLI approval requires an interactive terminal.");
        try { return await ApprovalRenderer.PromptAsync(request, cancellationToken); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return ApprovalResponse.Deny("Safe terminal input failed; approval was denied: " + error.Message); }
    }
}

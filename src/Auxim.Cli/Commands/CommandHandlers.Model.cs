using Auxim.Core.Runtime;
using Auxim.Cli.Interactive;

namespace Auxim.Cli;

public static partial class CommandHandlers
{
    public static int HandleModel(IReadOnlyList<string> args, IAuximRuntime runtime)
    {
        var subcommand = args.FirstOrDefault() ?? "show";
        return subcommand switch
        {
            "show" => ShowModel(runtime),
            "set" => SetModel(args.Skip(1).ToArray(), runtime),
            _ => PrintModelHelp(),
        };
    }

    private static int ShowModel(IAuximRuntime runtime)
    {
        var model = runtime.GetModelSettings();
        PrintModel(model);
        return 0;
    }

    private static int SetModel(IReadOnlyList<string> args, IAuximRuntime runtime)
    {
        if (args.Count == 0)
        {
            return SetModelInteractive(runtime);
        }

        if (args.Count < 2)
        {
            return PrintModelHelp();
        }

        var current = runtime.GetModelSettings();
        var updated = runtime.SetModelSettings(
            args[0],
            args[1],
            args.Count >= 3 ? args[2] : current.BaseUrl);
        Console.WriteLine($"Model saved to {updated.ConfigPath}");
        PrintModel(updated);
        return 0;
    }

    private static int SetModelInteractive(IAuximRuntime runtime)
    {
        var current = runtime.GetModelSettings();
        PrintPanel(
            "Auxim Model Setup",
            [
                $"Config: {current.ConfigPath}",
                $"Secrets: {current.SecretsPath}",
                "",
                $"Current provider: {current.Provider}",
                $"Current model:    {current.Model}",
                $"Current base URL: {current.BaseUrl ?? "(default)"}",
            ]);

        var selectedProvider = SelectProvider(current.Provider);
        var modelName = SelectModel(selectedProvider, current.Model);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            Console.Error.WriteLine("Model is required.");
            return 1;
        }

        var defaultBaseUrl = !string.IsNullOrWhiteSpace(current.BaseUrl)
            && string.Equals(selectedProvider.Id, current.Provider, StringComparison.OrdinalIgnoreCase)
                ? current.BaseUrl
                : selectedProvider.BaseUrl;
        var baseUrl = selectedProvider.IsCustom
            ? defaultBaseUrl
            : PromptWithDefault("Base URL", defaultBaseUrl);
        // The interactive shell can abort at any prompt, including hidden key entry.
        // Keep the standalone command's existing save order.
        var inShell = InteractiveInterrupt.Current is not null;
        var updated = inShell ? null : SaveModel();
        var credential = runtime.GetCredentialStatus(updated?.Provider ?? selectedProvider.Id);
        string? key = null;
        if (credential.Required
            && !credential.Configured
            && IsYes(PromptWithDefault("Save API key now?", "yes")))
        {
            key = ReadSecret($"{credential.EnvironmentVariable}: ");
        }

        if (inShell) TerminalInput.ThrowIfConfigurationCancelled();
        updated ??= SaveModel();
        if (!string.IsNullOrWhiteSpace(key))
        {
            runtime.SetApiKey(updated.Provider, key);
            credential = runtime.GetCredentialStatus(updated.Provider);
            Console.WriteLine($"API key saved to {credential.SecretsPath}");
        }

        PrintPanel(
            "Saved",
            [
                $"Provider: {updated.Provider}",
                $"Model:    {updated.Model}",
                $"Base URL: {updated.BaseUrl ?? "(default)"}",
                $"API key:  {FormatApiKeyStatus(credential)}",
                "",
                "Try: ./auxim chat \"hello\"",
            ]);
        return 0;

        AuximModelSettings SaveModel()
        {
            var settings = runtime.SetModelSettings(selectedProvider.Id, modelName, baseUrl);
            Console.WriteLine();
            Console.WriteLine($"Model saved to {settings.ConfigPath}");
            return settings;
        }
    }

    private static void PrintModel(AuximModelSettings model)
    {
        Console.WriteLine($"Provider: {model.Provider}");
        Console.WriteLine($"Model:    {model.Model}");
        Console.WriteLine($"Base URL: {model.BaseUrl ?? "(default)"}");
    }

    private static int PrintModelHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  auxim model show");
        Console.WriteLine("  auxim model set");
        Console.WriteLine("  auxim model set <provider> <model> [base-url]");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  auxim model set");
        Console.WriteLine("  auxim model set openai-compatible gpt-4o-mini https://api.openai.com/v1");
        return 1;
    }
}

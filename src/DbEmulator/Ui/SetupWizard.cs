using DbEmulator.Sync;

namespace DbEmulator.Ui;

/// <summary>Asks which live account to copy from and stores it encrypted for the current Windows user.</summary>
internal static class SetupWizard
{
    /// <summary>Returns true when new source settings were saved.</summary>
    public static bool Run(EmulatorRuntime runtime, ConsoleDashboard ui)
    {
        var current = runtime.ResolveSource(out var origin);
        Console.WriteLine();
        Console.WriteLine("  Source account: the live Azure Cosmos DB account to copy FROM. It is only ever read.");
        if (current is not null)
        {
            Console.WriteLine($"  Currently: {current}, from {origin}.");
            if (!origin.StartsWith("saved", StringComparison.Ordinal))
            {
                ui.Info($"This source is set in {origin}; change it there.");
                return false;
            }
        }

        Console.WriteLine("    1) Read-only connection string (recommended)");
        Console.WriteLine("       Azure Portal > your Cosmos DB account > Settings > Keys > Read-only Keys >");
        Console.WriteLine("       PRIMARY READ-ONLY CONNECTION STRING");
        Console.WriteLine("    2) Microsoft Entra ID (your az login / Visual Studio sign-in;");
        Console.WriteLine("       needs the 'Cosmos DB Built-in Data Reader' role on the account)");
        if (current is not null)
        {
            Console.WriteLine("    3) Forget the saved source");
        }

        Console.WriteLine("    Enter) cancel");
        switch (ui.Prompt("  Choice: "))
        {
            case "1":
                var connectionString = ui.ReadSecret("  Paste the read-only connection string: ");
                if (!SourceSettings.TryParseConnectionString(connectionString, out var settings, out var error))
                {
                    ui.Error(error);
                    return false;
                }

                SourceSecretStore.Save(runtime.Paths.SourceSecret, settings!);
                ui.Success($"Saved, encrypted for your Windows user. Source: {settings!.Host}.");
                ui.Info("Every request to Azure goes through a read-only guard: writes are blocked even if this were a read-write key.");
                return true;

            case "2":
                var endpoint = ui.Prompt("  Account endpoint (https://<account>.documents.azure.com:443/): ");
                if (SourceSettings.ValidateEndpoint(endpoint) is { } endpointError)
                {
                    ui.Error(endpointError);
                    return false;
                }

                var tenant = ui.Prompt("  Tenant id (Enter for your default tenant): ");
                SourceSecretStore.Save(runtime.Paths.SourceSecret,
                    new SourceSettings(endpoint, SourceAuthentication.EntraId, null, tenant.Length == 0 ? null : tenant));
                ui.Success($"Saved. Source: {new Uri(endpoint).Host} with Entra ID sign-in.");
                return true;

            case "3" when current is not null:
                SourceSecretStore.Delete(runtime.Paths.SourceSecret);
                ui.Info("Saved source removed.");
                return false;

            default:
                return false;
        }
    }
}

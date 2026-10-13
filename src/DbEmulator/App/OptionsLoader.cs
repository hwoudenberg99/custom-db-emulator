using System.Text.Json;
using DbEmulator.Storage;
using Microsoft.Extensions.Configuration;

namespace DbEmulator.App;

/// <summary>
/// Settings come from appsettings.json next to the exe, then data\settings.json (what you change in the app),
/// then DBEMULATOR_* environment variables.
/// </summary>
internal static class OptionsLoader
{
    public static (EmulatorOptions Options, string DataDirectory) Load(string baseDirectory, string? dataOverride, int? portOverride)
    {
        var first = Bind(baseDirectory, settingsFile: null);
        if (dataOverride is not null)
        {
            first.Storage.DataDirectory = dataOverride;
        }

        var dataDirectory = ResolveDataDirectory(baseDirectory, first.Storage.DataDirectory);
        var options = Bind(baseDirectory, SettingsFile(dataDirectory));
        options.Storage.DataDirectory = first.Storage.DataDirectory;
        if (portOverride is { } port)
        {
            options.Server.Port = port;
        }

        return (options, dataDirectory);
    }

    public static string SettingsFile(string dataDirectory) => Path.Combine(dataDirectory, "settings.json");

    /// <summary>Saves the settings that can be changed in the app.</summary>
    public static void Save(string dataDirectory, EmulatorOptions o)
    {
        var settings = new
        {
            Source = new { o.Source.Databases, o.Source.ExcludeContainers },
            Server = new { o.Server.Port, o.Server.AllowLocalWrites, o.Server.LogRequests, o.Server.EnforceTtl },
            Storage = new { o.Storage.KeepSnapshots, o.Storage.CompressSnapshots },
            Fetch = new { o.Fetch.OnStartupIfOlderThanHours, o.Fetch.AutoFetchEveryHours },
            App = new { o.App.CloseToTray },
        };
        Directory.CreateDirectory(dataDirectory);
        var file = SettingsFile(dataDirectory);
        var temp = file + ".tmp";
        DurableFile.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }

    private static EmulatorOptions Bind(string baseDirectory, string? settingsFile)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: true);
        if (settingsFile is not null && File.Exists(settingsFile))
        {
            builder.AddJsonFile(settingsFile, optional: true);
        }

        var options = new EmulatorOptions();
        builder.AddEnvironmentVariables("DBEMULATOR_").Build().Bind(options);
        return options;
    }

    private static string ResolveDataDirectory(string baseDirectory, string configured)
    {
        var dataDirectory = Path.GetFullPath(Path.Combine(baseDirectory, configured));
        if (!Path.IsPathRooted(configured) && !IsWritable(dataDirectory))
        {
            // e.g. the exe lives under Program Files
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbEmulator", configured);
        }

        return dataDirectory;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

internal static class FetchSchedule
{
    /// <summary>
    /// True when the newest copy is older than <paramref name="olderThanHours"/>. After a failed attempt it waits
    /// (up to an hour) before trying again, so a persistent error never turns into repeated reads of the live account.
    /// </summary>
    public static bool IsDue(EmulatorRuntime runtime, double olderThanHours)
    {
        if (olderThanHours <= 0)
        {
            return false;
        }

        var last = runtime.LastFetch;
        if (last is not null && DateTimeOffset.UtcNow - last.Manifest.CompletedUtc <= TimeSpan.FromHours(olderThanHours))
        {
            return false;
        }

        return runtime.State.LastAttempt is not { Succeeded: false } failed
            || DateTimeOffset.UtcNow - failed.StartedUtc > TimeSpan.FromHours(Math.Min(olderThanHours, 1));
    }
}

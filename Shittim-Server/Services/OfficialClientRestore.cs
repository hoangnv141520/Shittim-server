using System.Diagnostics;
using System.Text.Json;
using BlueArchiveAPI.Configuration;

namespace Shittim_Server.Services;

public static class OfficialClientRestore
{
    // Standalone: no listener, game login, resource download or player database access.
    public static int Run()
    {
        if (Process.GetProcessesByName("BlueArchive").Length > 0)
        {
            Console.Error.WriteLine("Close Blue Archive before switching servers.");
            return 1;
        }
        Config.Load();
        var metadata = ClientMetadataPatchService.GetMetadataPath();
        if (string.IsNullOrWhiteSpace(metadata) || !File.Exists(metadata))
        {
            Console.Error.WriteLine("Game client not found. Set ClientInstallDirectory in the Control Center configuration.");
            return 1;
        }
        var failures = new List<string>();
        var diagnostics = new RestoreDiagnostics(failures);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(diagnostics));
        var steps = new (string Name, Action Run)[]
        {
            ("IAS routing", new ClientNativeIasPatchService(factory.CreateLogger<ClientNativeIasPatchService>()).RestoreForOfficial),
            ("Steam", new ClientSteamOfflinePatchService(factory.CreateLogger<ClientSteamOfflinePatchService>()).RestoreForOfficial),
            ("Resource integrity", new ClientResourceIntegrityPatchService(factory.CreateLogger<ClientResourceIntegrityPatchService>()).RestoreForOfficial),
            ("Gateway key", new ClientMetadataPatchService(factory.CreateLogger<ClientMetadataPatchService>()).RestoreForOfficial),
            ("Inface config", new ClientInfaceConfigPatchService(factory.CreateLogger<ClientInfaceConfigPatchService>()).RestoreForOfficial),
            ("Store URL", new ClientStoreUrlPatchService(factory.CreateLogger<ClientStoreUrlPatchService>()).RestoreForOfficial),
            ("Region label", new ClientRegionLabelPatchService(factory.CreateLogger<ClientRegionLabelPatchService>()).RestoreForOfficial),
            ("Original NGS", new ClientGrap64ManagementService(factory.CreateLogger<ClientGrap64ManagementService>()).RestoreForOfficial)
        };
        foreach (var step in steps)
        {
            try { step.Run(); }
            catch (Exception ex) { failures.Add($"{step.Name}: {ex.Message}"); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ok = failures.Count == 0, errors = failures }));
        return failures.Count == 0 ? 0 : 1;
    }

    private sealed class RestoreDiagnostics(List<string> failures) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Warning) failures.Add(formatter(state, exception));
        }
        public void Dispose() { }
    }
}

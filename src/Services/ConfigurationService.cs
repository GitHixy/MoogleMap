using System;
using System.IO;
using MoogleMap.Models;
using Newtonsoft.Json;

namespace MoogleMap.Services;

/// <summary>
/// Service for managing plugin configuration
/// </summary>
public class ConfigurationService : IDisposable
{
    private readonly string configFilePath;
    private Configuration? configuration;

    /// <summary>
    /// True when no configuration file existed on disk, i.e. this is a first install.
    /// Used to keep the release notes from greeting brand-new users.
    /// </summary>
    public bool IsNewConfiguration { get; private set; }

    public Configuration Configuration => configuration ??= LoadConfiguration();

    public ConfigurationService()
    {
        var configDirectory = Plugin.PluginInterface.ConfigDirectory.FullName;
        configFilePath = Path.Combine(configDirectory, "config.json");

        // Ensure config directory exists
        Directory.CreateDirectory(configDirectory);
    }

    public void Save()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Configuration, Formatting.Indented);
            File.WriteAllText(configFilePath, json);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to save configuration to {ConfigPath}", configFilePath);
        }
    }

    private Configuration LoadConfiguration()
    {
        try
        {
            if (File.Exists(configFilePath))
            {
                var json = File.ReadAllText(configFilePath);
                var config = JsonConvert.DeserializeObject<Configuration>(json);
                if (config != null)
                {
                    Plugin.Log.Debug("Configuration loaded from {ConfigPath}", configFilePath);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to load configuration from {ConfigPath}", configFilePath);
        }

        Plugin.Log.Info("Creating new configuration");
        IsNewConfiguration = true;
        return new Configuration();
    }

    public void Dispose()
    {
        Save();
    }
}

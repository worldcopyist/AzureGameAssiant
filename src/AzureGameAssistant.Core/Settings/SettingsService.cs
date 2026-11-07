using System.Text.Json;

namespace AzureGameAssistant.Core.Settings;

public sealed record SettingsLoadResult(AppSettings Settings, bool Recovered, string? Warning);

public sealed class SettingsService
{
    private readonly object _gate = new();
    private readonly string _path;

    public SettingsService(string? path = null)
    {
        _path = path ?? AppPaths.SettingsPath;
    }

    public string SettingsPath => _path;

    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new SettingsLoadResult(new AppSettings(), false, null);
            }

            try
            {
                string json = File.ReadAllText(_path);
                AppSettings? settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);

                if (settings is null)
                {
                    return Recover("设置文件为空，已回退默认值");
                }

                if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
                {
                    Backup("unsupported");
                    return new SettingsLoadResult(
                        new AppSettings(),
                        true,
                        $"设置文件版本 {settings.SchemaVersion} 高于支持的 {AppSettings.CurrentSchemaVersion}，已回退默认值");
                }

                bool enableSensors = settings.SchemaVersion < 3;
                settings.Normalize();

                if (enableSensors)
                {
                    settings.Sensors.Enabled = true;
                }

                return new SettingsLoadResult(settings, false, null);
            }
            catch (JsonException ex)
            {
                return Recover($"设置文件解析失败：{ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                return Recover($"设置文件包含不支持的内容：{ex.Message}");
            }
            catch (IOException ex)
            {
                return new SettingsLoadResult(new AppSettings(), true, $"设置文件读取失败：{ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return new SettingsLoadResult(new AppSettings(), true, $"设置文件无访问权限：{ex.Message}");
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
                    stream.Flush(true);
                }

                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, null);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private SettingsLoadResult Recover(string warning)
    {
        Backup("corrupt");
        return new SettingsLoadResult(new AppSettings(), true, warning);
    }

    private void Backup(string kind)
    {
        try
        {
            string directory = Path.GetDirectoryName(_path) ?? AppPaths.RootDirectory;
            string name = Path.GetFileNameWithoutExtension(_path);
            string backup = Path.Combine(
                directory,
                $"{name}.{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            if (File.Exists(_path))
            {
                File.Move(_path, backup, overwrite: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

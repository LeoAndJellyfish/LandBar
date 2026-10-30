using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LandBar.Plugins;

internal static partial class PluginProtocol
{
    internal const int Version = 1;
    internal const int MaximumLineLength = 16 * 1024;
    internal const int MaximumCardsPerPlugin = 8;
    internal const int MaximumCards = 32;
    internal const int MaximumRunning = 8;
    internal static readonly HashSet<string> Icons = new(StringComparer.Ordinal)
        { "activity", "timer", "check", "download", "code", "calendar", "info", "music" };
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 12 };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdentifier();
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    internal static bool IsPluginId(string value) => PluginIdentifier().IsMatch(value);

    internal static PluginManifest ReadManifest(string directory)
    {
        RejectLinks(directory);
        string file = Path.Combine(directory, "plugin.json");
        RejectLinks(file);
        if (new FileInfo(file).Length > MaximumLineLength)
            throw new InvalidDataException("插件清单超过 16 KiB。");
        using var json = Parse(File.ReadAllText(file, Encoding.UTF8));
        var root = json.RootElement;
        string id = Text(root, "id", 64);
        if (!IsPluginId(id)) throw new InvalidDataException("插件 id 格式无效。");
        string command = Text(root, "command", 1024);
        if (command.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("插件启动命令无效。");
        // PATH executable names and explicit absolute executables are permitted. Relative executable
        // paths belong to this plugin directory; shell parsing and parent-directory traversal are absent.
        if (!Path.IsPathRooted(command) && (command.Contains('/') || command.Contains('\\')))
        {
            string executable = Path.GetFullPath(Path.Combine(directory, command));
            if (!IsWithin(executable, directory)) throw new InvalidDataException("插件可执行文件超出插件目录。");
            CheckDescendants(directory, executable);
        }
        if (Path.IsPathRooted(command) && !Path.IsPathFullyQualified(command))
            throw new InvalidDataException("插件可执行文件必须使用完整路径。");
        if (root.GetProperty("protocolVersion").GetInt32() != Version)
            throw new InvalidDataException("插件协议版本不受支持。");
        if (!root.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 32)
            throw new InvalidDataException("arguments 必须是至多 32 项的字符串数组。");
        string[] args = arguments.EnumerateArray().Select(value =>
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } item || item.Length > 1024 || item.Contains('\0'))
                throw new InvalidDataException("插件参数无效。");
            return item;
        }).ToArray();
        return new(id, Text(root, "name", 64), Text(root, "version", 64),
            Text(root, "description", 240, allowEmpty: true), command, args, Version);
    }

    internal static JsonDocument Parse(string line)
    {
        if (line.Length > MaximumLineLength || Encoding.UTF8.GetByteCount(line) > MaximumLineLength)
            throw new InvalidDataException("插件消息超过 16 KiB。");
        var json = JsonDocument.Parse(line, JsonOptions);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            json.Dispose();
            throw new InvalidDataException("插件消息必须是 JSON 对象。");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
            if (!names.Add(property.Name))
            {
                json.Dispose();
                throw new InvalidDataException("插件消息包含重复字段。");
            }
        return json;
    }

    internal static PluginCard Card(string pluginId, JsonElement root, DateTimeOffset now)
    {
        string id = CardId(root);
        string icon = root.TryGetProperty("icon", out _) ? Text(root, "icon", 20) : "activity";
        if (!Icons.Contains(icon)) throw new InvalidDataException("插件图标不受支持。");
        double? progress = null;
        if (root.TryGetProperty("progress", out var progressValue) && progressValue.ValueKind != JsonValueKind.Null)
        {
            if (!progressValue.TryGetDouble(out double number) || !double.IsFinite(number) || number < 0 || number > 1)
                throw new InvalidDataException("progress 必须介于 0 与 1 之间。");
            progress = number;
        }
        DateTimeOffset? expires = null;
        if (root.TryGetProperty("ttlSeconds", out var ttl) && ttl.ValueKind != JsonValueKind.Null)
        {
            if (!ttl.TryGetDouble(out double seconds) || !double.IsFinite(seconds) || seconds < 1 || seconds > 86400)
                throw new InvalidDataException("ttlSeconds 必须介于 1 与 86400 秒之间。");
            expires = now.AddSeconds(seconds);
        }
        var actions = new List<PluginAction>();
        if (root.TryGetProperty("actions", out var actionValues))
        {
            if (actionValues.ValueKind != JsonValueKind.Array || actionValues.GetArrayLength() > 3)
                throw new InvalidDataException("插件卡片最多支持三个操作。");
            foreach (var value in actionValues.EnumerateArray())
            {
                string actionId = Text(value, "id", 64);
                if (!Identifier().IsMatch(actionId) || actions.Any(action => action.Id == actionId))
                    throw new InvalidDataException("插件操作 id 无效或重复。");
                actions.Add(new(actionId, Text(value, "label", 24)));
            }
        }
        return new(pluginId, id, Text(root, "title", 120), Text(root, "body", 600, true),
            icon, progress, actions.AsReadOnly(), now, expires);
    }

    internal static string CardId(JsonElement root)
    {
        string id = Text(root, "id", 64);
        if (!Identifier().IsMatch(id)) throw new InvalidDataException("插件卡片 id 格式无效。");
        return id;
    }

    internal static string Text(JsonElement root, string name, int maximumLength, bool allowEmpty = false)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length > maximumLength || (!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0'))
            throw new InvalidDataException($"插件字段 {name} 无效。");
        return text;
    }

    internal static bool IsWithin(string path, string parent) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("插件目录和清单不支持符号链接或目录联接。");
    }

    private static void CheckDescendants(string directory, string executable)
    {
        string current = directory;
        foreach (string component in Path.GetRelativePath(directory, executable).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current)) RejectLinks(current);
        }
    }
}

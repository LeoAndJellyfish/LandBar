using System.Text.Json;
using System.Text.RegularExpressions;

namespace LandBar.Plugins;

internal static partial class PluginProtocol
{
    private const int MaximumViewNodes = 128;
    private const int MaximumViewDepth = 8;
    private static readonly HashSet<string> NodeTypes = new(StringComparer.Ordinal)
        { "column", "row", "text", "button", "ring", "sprite", "spacer" };
    private static readonly HashSet<string> NodeFields = new(StringComparer.Ordinal)
    {
        "type", "id", "text", "color", "background", "width", "height", "fontSize", "weight", "align",
        "spacing", "padding", "cornerRadius", "value", "trackColor", "thickness", "actionId", "children",
        "pixelWidth", "pixelHeight", "palette", "frames", "fps"
    };

    [GeneratedRegex("^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    internal static PluginView View(string pluginId, JsonElement root, DateTimeOffset now)
    {
        string id = CardId(root);
        string slot = Text(root, "slot", 32);
        if (!PluginSlots.All.Contains(slot)) throw new InvalidDataException("插件页面插槽不受支持。");
        string? title = OptionalText(root, "title", 24);
        if (slot == "activity.tab" && title is null)
            throw new InvalidDataException("activity.tab 页面必须提供 title。");
        if (title is not null)
        {
            if (title.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
                throw new InvalidDataException("插件页面 title 必须是单行文本，最多 24 个 UTF-16 代码单元。");
            title = title.Trim();
        }
        if (!root.TryGetProperty("root", out var node)) throw new InvalidDataException("插件页面缺少 root 节点。");
        int count = 0;
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var tree = ReadNode(node, 1, ref count, nodeIds);
        if (slot.StartsWith("idle.", StringComparison.Ordinal))
        {
            double maximumWidth = slot == "idle.content" ? 400 : 64;
            if (tree.Width > maximumWidth || tree.Height > 36)
                throw new InvalidDataException($"{slot} 根节点最大宽度为 {maximumWidth}，最大高度为 36。");
        }
        return new(pluginId, id, slot, tree, now, title);
    }

    private static PluginNode ReadNode(JsonElement json, int depth, ref int count, HashSet<string> nodeIds)
    {
        if (depth > MaximumViewDepth || ++count > MaximumViewNodes)
            throw new InvalidDataException("每个插件页面最多包含 128 个节点，深度最多为 8。");
        if (json.ValueKind != JsonValueKind.Object) throw new InvalidDataException("插件页面节点必须是对象。");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in json.EnumerateObject())
            if (!fields.Add(field.Name) || !NodeFields.Contains(field.Name))
                throw new InvalidDataException("插件页面节点包含重复或不支持的字段。");
        string type = Text(json, "type", 12);
        if (!NodeTypes.Contains(type)) throw new InvalidDataException("插件页面节点类型不受支持。");
        string? id = OptionalText(json, "id", 64);
        if (id is not null && (!Identifier().IsMatch(id) || !nodeIds.Add(id)))
            throw new InvalidDataException("插件页面节点 id 无效或重复。");
        string? actionId = OptionalText(json, "actionId", 64);
        if (type == "button" && (actionId is null || !Identifier().IsMatch(actionId)))
            throw new InvalidDataException("插件页面按钮必须包含有效的 actionId。");
        if (type != "button" && actionId is not null)
            throw new InvalidDataException("仅 button 节点支持 actionId。");
        var children = new List<PluginNode>();
        if (json.TryGetProperty("children", out var childValues))
        {
            if (type is not ("row" or "column") || childValues.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("仅 row 和 column 节点支持 children 数组。");
            foreach (var child in childValues.EnumerateArray()) children.Add(ReadNode(child, depth + 1, ref count, nodeIds));
        }
        string weight = OptionalText(json, "weight", 12) ?? "normal";
        if (weight is not ("normal" or "semibold")) throw new InvalidDataException("weight 仅支持 normal 和 semibold。");
        string align = OptionalText(json, "align", 12) ?? "start";
        if (align is not ("start" or "center" or "end" or "stretch"))
            throw new InvalidDataException("align 仅支持 start、center、end 和 stretch。");
        double? value = Number(json, "value", 0, 1);
        if (type == "ring" && value is null) throw new InvalidDataException("ring 节点需要 0 至 1 之间的 value。");
        PluginSprite? sprite = null;
        if (type == "sprite") sprite = ReadSprite(json);
        else if (json.TryGetProperty("pixelWidth", out _) || json.TryGetProperty("pixelHeight", out _) ||
                 json.TryGetProperty("palette", out _) || json.TryGetProperty("frames", out _) || json.TryGetProperty("fps", out _))
            throw new InvalidDataException("像素动画字段仅适用于 sprite 节点。");
        return new PluginNode
        {
            Type = type, Id = id, Text = OptionalText(json, "text", 512, allowEmpty: true) ?? "",
            Color = ReadColor(json, "color"), Background = ReadColor(json, "background"),
            Width = Number(json, "width", 0, 720), Height = Number(json, "height", 0, 420),
            FontSize = Number(json, "fontSize", 12, 32) ?? 15, Weight = weight, Align = align,
            Spacing = Number(json, "spacing", 0, 32) ?? 0, Padding = Number(json, "padding", 0, 32) ?? 0,
            CornerRadius = Number(json, "cornerRadius", 0, 64) ?? 0, Value = value,
            TrackColor = ReadColor(json, "trackColor"), Thickness = Number(json, "thickness", 1, 16) ?? 3,
            ActionId = actionId, Children = children.AsReadOnly(), Sprite = sprite
        };
    }

    private static PluginSprite ReadSprite(JsonElement json)
    {
        int width = PixelDimension(json, "pixelWidth"), height = PixelDimension(json, "pixelHeight");
        if (!json.TryGetProperty("palette", out var colors) || colors.ValueKind != JsonValueKind.Array || colors.GetArrayLength() is < 1 or > 16)
            throw new InvalidDataException("sprite palette 必须包含 1 至 16 项颜色。");
        var palette = colors.EnumerateArray().Select(color =>
        {
            if (color.ValueKind != JsonValueKind.String || color.GetString() is not { } text || !HexColor().IsMatch(text))
                throw new InvalidDataException("sprite palette 颜色必须使用 #RRGGBB 或 #AARRGGBB。");
            return text.ToUpperInvariant();
        }).ToArray();
        if (!json.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() is < 1 or > 16)
            throw new InvalidDataException("sprite frames 必须包含 1 至 16 帧。");
        var values = frames.EnumerateArray().Select(frame =>
        {
            if (frame.ValueKind != JsonValueKind.String || frame.GetString() is not { } pixels || pixels.Length != width * height)
                throw new InvalidDataException("sprite 每帧必须包含 pixelWidth × pixelHeight 个像素索引。");
            foreach (char pixel in pixels)
            {
                int index = pixel is >= '0' and <= '9' ? pixel - '0' : pixel is >= 'A' and <= 'F' ? pixel - 'A' + 10 : pixel is >= 'a' and <= 'f' ? pixel - 'a' + 10 : -1;
                if (index < 0 || index >= palette.Length) throw new InvalidDataException("sprite 像素索引超出 palette 范围。");
            }
            return pixels.ToUpperInvariant();
        }).ToArray();
        return new(width, height, Array.AsReadOnly(palette), Array.AsReadOnly(values), Number(json, "fps", 1, 12) ?? 6);
    }

    private static int PixelDimension(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int size) || size is < 1 or > 32)
            throw new InvalidDataException($"sprite {name} 必须是 1 至 32 的整数。");
        return size;
    }

    private static string? OptionalText(JsonElement json, string name, int maximumLength, bool allowEmpty = false)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return Text(json, name, maximumLength, allowEmpty);
    }

    private static string? ReadColor(JsonElement json, string name)
    {
        string? value = OptionalText(json, name, 9);
        if (value is not null && !HexColor().IsMatch(value)) throw new InvalidDataException($"{name} 必须使用 #RRGGBB 或 #AARRGGBB。");
        return value?.ToUpperInvariant();
    }

    private static double? Number(JsonElement json, string name, double minimum, double maximum)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number) || number < minimum || number > maximum)
            throw new InvalidDataException($"{name} 必须介于 {minimum} 与 {maximum} 之间。");
        return number;
    }

    internal static bool SameNode(PluginNode left, PluginNode right) =>
        left.Type == right.Type && left.Id == right.Id && left.Text == right.Text && left.Color == right.Color &&
        left.Background == right.Background && left.Width == right.Width && left.Height == right.Height &&
        left.FontSize == right.FontSize && left.Weight == right.Weight && left.Align == right.Align &&
        left.Spacing == right.Spacing && left.Padding == right.Padding && left.CornerRadius == right.CornerRadius &&
        left.Value == right.Value && left.TrackColor == right.TrackColor && left.Thickness == right.Thickness &&
        left.ActionId == right.ActionId && SameSprite(left.Sprite, right.Sprite) &&
        left.Children.Count == right.Children.Count && left.Children.Zip(right.Children).All(pair => SameNode(pair.First, pair.Second));

    private static bool SameSprite(PluginSprite? left, PluginSprite? right) => left is null ? right is null :
        right is not null && left.PixelWidth == right.PixelWidth && left.PixelHeight == right.PixelHeight &&
        left.Fps == right.Fps && left.Palette.SequenceEqual(right.Palette) && left.Frames.SequenceEqual(right.Frames);

    internal static bool HasViewAction(PluginNode node, string actionId) =>
        node.Type == "button" && node.ActionId == actionId || node.Children.Any(child => HasViewAction(child, actionId));
}

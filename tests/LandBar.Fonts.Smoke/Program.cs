using System.Text.Json;
using System.Windows;
using System.Windows.Media;

// These resources are linked directly from the application font folder. Nothing is installed.
_ = new Application();
var family = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Noto Sans SC");
int checks = 0;
foreach (var weight in new[] { FontWeights.Normal, FontWeights.SemiBold })
{
    int expected = weight.ToOpenTypeWeight();
    var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
    Require(typeface.TryGetGlyphTypeface(out var glyphs), $"Weight {expected} loads");
    Require(glyphs!.FontUri.Scheme == "pack", $"Weight {expected} uses an embedded resource");
    Require(glyphs.FamilyNames.Values.Contains("Noto Sans SC"), $"Weight {expected} has the intended family");
    Require(glyphs.Weight.ToOpenTypeWeight() == expected, $"Weight {expected} is exact");
    Require(new[] { 0x4E2D, 0x6587, 0x97F3, 0x4E50, 0x8BBE, 0x7F6E }.All(glyphs.CharacterToGlyphMap.ContainsKey),
        $"Weight {expected} contains representative Chinese glyphs");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        requestedWeight = expected,
        actualWeight = glyphs.Weight.ToOpenTypeWeight(),
        family = glyphs.FamilyNames.Values.First(),
        face = glyphs.FaceNames.Values.First(),
        fontUri = glyphs.FontUri.ToString(),
    }));
}
Console.WriteLine($"{checks}/{checks} font checks passed");

void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS " + description);
}

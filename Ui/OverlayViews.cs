using System.Globalization;
using System.Numerics;
using ImGuiNET;

// An alert shown in the center window; Started is a Stopwatch timestamp.
internal sealed record ActiveAlert(TimerDefinition Timer, Encounter Encounter, long Started);

// Contents of the two floating windows: RaidAbilityTimeline-style bars and BigWigs-style center text.
internal static class OverlayViews
{
    internal static readonly Vector4 DefaultBarColor = new(0.27f, 0.55f, 0.85f, 1f);
    internal static readonly Vector4 WarningBarColor = new(0.90f, 0.42f, 0.18f, 1f);
    private static readonly Vector4 DefaultTrackColor = new(0.05f, 0.05f, 0.06f, 0.75f);
    private static readonly Vector4 DefaultShadowColor = new(0, 0, 0, 192 / 255f);

    internal static Vector4 ParseColor(string? value, Vector4 fallback)
    {
        if (value is null || !EncounterYaml.IsColor(value)) return fallback;
        uint rgb = uint.Parse(value.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        float alpha = value.Length == 9
            ? uint.Parse(value.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f : 1f;
        return new Vector4((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
    }

    internal static string FormatRemaining(double seconds) => seconds < 10
        ? seconds.ToString("0.0", CultureInfo.InvariantCulture)
        : seconds < 60 ? Math.Ceiling(seconds).ToString("0", CultureInfo.InvariantCulture)
        : TimeSpan.FromSeconds(Math.Ceiling(seconds)).ToString(seconds < 3600 ? @"m\:ss" : @"h\:mm\:ss", CultureInfo.InvariantCulture);

    // Bars sorted by end time, soonest first. A bar empties as its timer runs out and turns to the
    // warning color (pulsing) once its alert has fired.
    internal static void DrawTimeline(Vector2 size, IReadOnlyList<TimerView> timers, long now, long frequency,
        int maxBars, IconCache icons, Encounter? encounter, bool showPlaceholder, TimelineColors? monochrome = null)
    {
        var draw = ImGui.GetWindowDrawList();
        float line = ImGui.GetTextLineHeight();
        float barHeight = MathF.Round(line * 1.45f), gap = MathF.Max(2, MathF.Round(line * 0.2f));
        var padding = ImGui.GetStyle().WindowPadding;
        float y = padding.Y, left = padding.X, right = size.X - padding.X;
        if (timers.Count == 0 && showPlaceholder)
        {
            draw.AddText(new Vector2(left, y), ImGui.GetColorU32(ImGuiCol.TextDisabled), Localization.T("Timeline (drag to move)"));
            return;
        }
        double pulse = 0.5 + 0.5 * Math.Sin(now / (double)frequency * Math.PI * 4);
        // Monochrome replaces every timer's YAML color; otherwise the fixed track, text and shadow colors.
        Vector4? monoFill = null;
        Vector4 flash = WarningBarColor, track = DefaultTrackColor, textColor = Vector4.One, shadowColor = DefaultShadowColor;
        if (monochrome is not null)
        {
            monoFill = ParseColor(monochrome.Fill, DefaultBarColor);
            flash = ParseColor(monochrome.Flash, WarningBarColor);
            track = ParseColor(monochrome.Track, DefaultTrackColor);
            textColor = ParseColor(monochrome.Text, Vector4.One);
            shadowColor = ParseColor(monochrome.Shadow, DefaultShadowColor);
        }
        uint trackU32 = ImGui.GetColorU32(track), text = ImGui.GetColorU32(textColor), shadow = ImGui.GetColorU32(shadowColor);
        for (int i = 0; i < timers.Count && i < maxBars; i++)
        {
            var timer = timers[i];
            if (y + barHeight > size.Y - padding.Y + 0.5f) break;
            double remaining = timer.Remaining(now, frequency);
            float fraction = (float)Math.Clamp(remaining / timer.Timer.Duration, 0, 1);
            // YAML colors are drawn at 90% alpha; monochrome colors carry their own alpha.
            float fillAlpha = monoFill is null ? 0.9f : 1f;
            Vector4 color = timer.Alerted ? Vector4.Lerp(flash, Vector4.One, (float)(pulse * 0.25)) with { W = flash.W }
                : monoFill ?? ParseColor(timer.Timer.Color, DefaultBarColor);
            color.W *= fillAlpha;
            var min = new Vector2(left, y);
            var max = new Vector2(right, y + barHeight);
            // The icon column is always reserved, so bars line up whether or not a timer has an icon.
            float x = left + barHeight + gap;
            if (icons.Get(encounter?.Resolve(timer.Timer.Icon)) is { } icon)
                DrawIcon(draw, icon, min, new Vector2(left + barHeight, max.Y),
                    monoFill is { } fill ? fill with { W = 1 } : IconTint(timer.Timer.Color), 1f);
            draw.AddRectFilled(new Vector2(x, y), max, trackU32, 3f);
            if (fraction > 0)
                draw.AddRectFilled(new Vector2(x, y), new Vector2(x + (right - x) * fraction, max.Y), ImGui.GetColorU32(color), 3f);
            string time = FormatRemaining(remaining);
            float timeWidth = ImGui.CalcTextSize(time).X;
            float textY = y + (barHeight - line) / 2;
            string label = $"{timer.Timer.DisplayName}";
            draw.PushClipRect(new Vector2(x, y), new Vector2(right - timeWidth - 10, max.Y), true);
            draw.AddText(new Vector2(x + 7, textY + 1), shadow, label);
            draw.AddText(new Vector2(x + 6, textY), text, label);
            draw.PopClipRect();
            draw.AddText(new Vector2(right - timeWidth - 5, textY + 1), shadow, time);
            draw.AddText(new Vector2(right - timeWidth - 6, textY), text, time);
            y += barHeight + gap;
        }
    }

    // An image icon is drawn as it is. A Lucide glyph is coverage only: it takes the tint, over a dark
    // shadow so it stays readable on any background.
    private static void DrawIcon(ImDrawListPtr draw, GpuTexture icon, Vector2 min, Vector2 max, Vector4 tint, float alpha)
    {
        if (!icon.AlphaOnly)
        {
            draw.AddImage(icon.Id, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, alpha)));
            return;
        }
        var shadow = new Vector2(MathF.Max(1, (max.X - min.X) / 24));
        draw.AddImage(icon.Id, min + shadow, max + shadow, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.8f * alpha)));
        draw.AddImage(icon.Id, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(tint with { W = tint.W * alpha }));
    }

    // A Lucide icon's color: the timer's color, opaque, or the default bar color.
    internal static Vector4 IconTint(string? color) => ParseColor(color, DefaultBarColor) with { W = 1 };

    // Newest alert on top; each one holds for its lifetime and fades out over the last half second.
    internal static void DrawAlerts(Vector2 size, IReadOnlyList<ActiveAlert> alerts, long now, long frequency,
        float lifetime, IconCache icons, bool showPlaceholder)
    {
        var draw = ImGui.GetWindowDrawList();
        float line = ImGui.GetTextLineHeight();
        float y = ImGui.GetStyle().WindowPadding.Y;
        if (alerts.Count == 0 && showPlaceholder)
        {
            string hint = Localization.T("Alerts (drag to move)");
            draw.AddText(new Vector2((size.X - ImGui.CalcTextSize(hint).X) / 2, (size.Y - line) / 2),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), hint);
            return;
        }
        for (int i = alerts.Count - 1; i >= 0 && y + line <= size.Y; i--)
        {
            var alert = alerts[i];
            float age = (float)((now - alert.Started) / (double)frequency);
            float alpha = Math.Clamp((lifetime - age) / 0.5f, 0f, 1f);
            // A short pop-in: start slightly larger and settle within 150 ms.
            float scale = 1f + 0.15f * Math.Clamp(1f - age / 0.15f, 0f, 1f);
            string text = alert.Timer.AlertText;
            var color = ParseColor(alert.Timer.Color, new Vector4(1f, 0.82f, 0.25f, 1f));
            var font = ImGui.GetFont();
            float fontSize = ImGui.GetFontSize() * scale;
            var textSize = font.CalcTextSizeA(fontSize, float.MaxValue, 0, text);
            var icon = icons.Get(alert.Encounter.Resolve(alert.Timer.Icon));
            float iconSize = icon is null ? 0 : fontSize;
            float total = textSize.X + (icon is null ? 0 : iconSize + fontSize * 0.25f);
            var origin = new Vector2((size.X - total) / 2, y + (line * 1.2f - textSize.Y) / 2);
            if (icon is not null)
            {
                DrawIcon(draw, icon, origin, origin + new Vector2(iconSize), color, alpha);
                origin.X += iconSize + fontSize * 0.25f;
            }
            uint outline = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.85f * alpha));
            float o = MathF.Max(1, fontSize / 22);
            foreach (var offset in new[] { new Vector2(-o, 0), new Vector2(o, 0), new Vector2(0, -o), new Vector2(0, o), new Vector2(o, o) })
                draw.AddText(font, fontSize, origin + offset, outline, text);
            draw.AddText(font, fontSize, origin, ImGui.GetColorU32(color with { W = color.W * alpha }), text);
            y += line * 1.2f;
        }
    }
}

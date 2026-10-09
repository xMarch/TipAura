using System.Numerics;
using ImGuiNET;

// Scrollable read-only text for long license files. Lines are word-wrapped once per text/width change
// and drawn through ImGuiListClipper, so a 6000-line notice costs only the visible rows each frame.
internal sealed class LicenseTextView
{
    private string? _source;
    private int _columns;
    private string[] _lines = [];

    internal void Draw(string id, string text)
    {
        if (ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.Borders))
        {
            float glyph = Math.Max(1f, ImGui.CalcTextSize("M").X);
            int columns = Math.Max(20, (int)(ImGui.GetContentRegionAvail().X / glyph));
            if (!ReferenceEquals(text, _source) || columns != _columns)
            {
                _lines = Wrap(text, columns);
                _source = text;
                _columns = columns;
            }
            unsafe
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(_lines.Length);
                while (clipper.Step())
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        ImGui.TextUnformatted(_lines[i]);
                clipper.End();
                clipper.Destroy();
            }
        }
        ImGui.EndChild();
    }

    internal static string[] Wrap(string text, int columns)
    {
        var lines = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd().Replace('\t', ' ');
            int indent = line.Length - line.TrimStart().Length;
            // Continuation rows keep the paragraph's indent unless it would leave no room for text.
            string prefix = indent < columns / 2 ? new string(' ', indent) : "";
            while (line.Length > columns)
            {
                int split = line.LastIndexOf(' ', columns);
                if (split <= indent) split = columns;
                lines.Add(line[..split].TrimEnd());
                line = prefix + line[split..].TrimStart();
            }
            lines.Add(line);
        }
        return [.. lines];
    }
}

using System.Numerics;
using ImGuiNET;

internal enum TitleBarAction { None, Minimize, Close }

internal sealed class TitleBarButtons
{
    private TitleBarAction _pressed;
    internal const uint HoverColor = 0xFF2311E8; // ImGui packed RGBA: #E81123

    internal TitleBarAction Draw(Vector2 origin, float width, float height)
    {
        var draw = ImGui.GetWindowDrawList();
        TitleBarAction result = TitleBarAction.None;
        for (int index = 0; index < 2; index++)
        {
            var action = index == 0 ? TitleBarAction.Minimize : TitleBarAction.Close;
            var min = origin + new Vector2(width - height * (2 - index), 0);
            var max = min + new Vector2(height);
            bool hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max, false);
            draw.PushClipRect(min, max, false);
            if (hovered) draw.AddRectFilled(min, max, action == TitleBarAction.Close
                ? HoverColor : ImGui.GetColorU32(ImGuiCol.ButtonHovered));
            uint foreground = hovered ? 0xFFFFFFFF : ImGui.GetColorU32(ImGuiCol.Text);
            var a = min + new Vector2(height * 0.3f);
            var b = min + new Vector2(height * 0.7f);
            if (action == TitleBarAction.Close)
            {
                draw.AddLine(a, b, foreground, 1f);
                draw.AddLine(new Vector2(a.X, b.Y), new Vector2(b.X, a.Y), foreground, 1f);
            }
            else draw.AddLine(new Vector2(a.X, min.Y + height * 0.65f),
                new Vector2(b.X, min.Y + height * 0.65f), foreground, 1f);
            draw.PopClipRect();
            if (hovered)
            {
                ImGui.SetTooltip(Localization.T(action == TitleBarAction.Close ? "Close" : "Minimize"));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _pressed = action;
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && _pressed == action) result = action;
            }
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _pressed = TitleBarAction.None;
        return result;
    }
}

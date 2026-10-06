using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MoogleMap.UI;

namespace MoogleMap.Windows;

/// <summary>
/// Base window class with ImGui support.
/// Applies the MoogleMap theme around every window so the frame, title bar and
/// scrollbars share the same look as the content.
/// </summary>
public abstract class Window : IDisposable
{
    protected string WindowName { get; }

    private bool isOpen = false;

    public bool IsOpen
    {
        get => isOpen;
        set => isOpen = value;
    }

    protected ImGuiWindowFlags WindowFlags { get; set; } = ImGuiWindowFlags.None;
    protected Vector2? Size { get; set; } = null;
    protected Vector2? SizeConstraintMin { get; set; } = null;
    protected Vector2? SizeConstraintMax { get; set; } = null;
    public float? BgAlpha { get; set; } = null;

    /// <summary>Paints the themed gradient backdrop behind the contents.</summary>
    protected bool DrawBackdrop { get; set; } = true;

    /// <summary>Centres the window on the viewport each time it appears.</summary>
    protected bool CenterOnAppearing { get; set; } = false;

    /// <summary>
    /// Gate for windows that only make sense in-game. Returning false skips the frame
    /// entirely without touching <see cref="IsOpen"/>, so the user's intent is preserved
    /// across the title screen and character switches.
    /// </summary>
    protected virtual bool ShouldDraw => true;

    /// <summary>Called the frame the user closes the window with its title-bar button.</summary>
    protected virtual void OnClosed() { }

    protected Window(string name)
    {
        WindowName = name;
    }

    public void Draw()
    {
        if (!IsOpen || !ShouldDraw) return;

        try
        {
            // The theme has to be pushed before Begin so the window chrome picks it up.
            using var theme = Theme.Push();

            if (SizeConstraintMin.HasValue && SizeConstraintMax.HasValue)
            {
                ImGui.SetNextWindowSizeConstraints(SizeConstraintMin.Value, SizeConstraintMax.Value);
            }

            if (Size.HasValue)
            {
                ImGui.SetNextWindowSize(Size.Value, ImGuiCond.FirstUseEver);
            }

            if (BgAlpha.HasValue)
            {
                ImGui.SetNextWindowBgAlpha(BgAlpha.Value);
            }

            if (CenterOnAppearing)
            {
                var center = ImGui.GetMainViewport().GetCenter();
                ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            }

            var wasOpen = isOpen;

            if (ImGui.Begin(WindowName, ref isOpen, WindowFlags))
            {
                // Contents are guarded separately so a draw error can never skip End()
                // and leave ImGui's window stack unbalanced.
                try
                {
                    if (DrawBackdrop)
                        Theme.DrawWindowBackdrop();

                    DrawContents();
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Error drawing contents of {WindowName}", WindowName);
                }
            }
            ImGui.End();

            if (wasOpen && !isOpen)
                OnClosed();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing window {WindowName}", WindowName);
        }
    }

    protected abstract void DrawContents();

    public virtual void Dispose()
    {
        // Override in derived classes if needed
    }
}

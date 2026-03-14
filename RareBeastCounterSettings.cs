using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace RareBeastCounter;

public class RareBeastCounterSettings : ISettings
{
    [Menu("Enable", "Enable or disable the Rare Beast Counter plugin.")]
    public ToggleNode Enable { get; set; } = new(false);

    [Menu("Visibility", "When the counter and completed message are allowed to render.")]
    public VisibilitySettings Visibility { get; set; } = new();

    [Menu("Counter Window", "Main on-screen counter window settings.")]
    public CounterWindowSettings CounterWindow { get; set; } = new();

    [Menu("Counter (Completed State)", "How the main counter looks when all beasts are found.")]
    public CompletedCounterSettings CompletedCounter { get; set; } = new();

    [Menu("Completed Message Window", "Separate window shown after all beasts are found.")]
    public CompletedMessageWindowSettings CompletedMessageWindow { get; set; } = new();
}

[Submenu(CollapsedByDefault = false)]
public class VisibilitySettings
{
    [Menu("Hide In Hideout", "Do not show counter/message while inside hideouts.")]
    public ToggleNode HideInHideout { get; set; } = new(true);

    [Menu("Hide On Fullscreen Panels", "Do not show counter/message while any fullscreen UI panel is visible.")]
    public ToggleNode HideOnFullscreenPanels { get; set; } = new(true);
}

[Submenu(CollapsedByDefault = false)]
public class CounterWindowSettings
{
    [Menu("X Position (%)", "Horizontal position of the main counter window.")]
    public RangeNode<float> XPos { get; set; } = new(50, 0, 100);

    [Menu("Y Position (%)", "Vertical position of the main counter window.")]
    public RangeNode<float> YPos { get; set; } = new(10, 0, 100);

    [Menu("Padding", "Inner spacing between counter text and window border.")]
    public RangeNode<float> Padding { get; set; } = new(6, 0, 50);

    [Menu("Border Thickness", "Border thickness of the main counter window.")]
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 10);

    [Menu("Border Rounding", "Corner roundness of the main counter window.")]
    public RangeNode<float> BorderRounding { get; set; } = new(0, 0, 25);

    [Menu("Text Scale", "Text scale for the main counter in normal state.")]
    public RangeNode<float> TextScale { get; set; } = new(1f, 0.5f, 4f);

    [Menu("Text Color", "Text color of the main counter in normal state.")]
    public ColorNode TextColor { get; set; } = new(new Color(255, 180, 70, 255));

    [Menu("Border Color", "Border color of the main counter in normal state.")]
    public ColorNode BorderColor { get; set; } = new(Color.Black);

    [Menu("Background Color", "Background color of the main counter window.")]
    public ColorNode BackgroundColor { get; set; } = new(new Color(0, 0, 0, 180));
}

[Submenu(CollapsedByDefault = false)]
public class CompletedCounterSettings
{
    [Menu("Show While Not Complete", "Testing mode: use completed counter style even before all beasts are found.")]
    public ToggleNode ShowWhileNotComplete { get; set; } = new(false);

    [Menu("Text Scale", "Text scale for the main counter when all beasts are found.")]
    public RangeNode<float> TextScale { get; set; } = new(1.8f, 0.5f, 6f);

    [Menu("Text Color", "Text color for the main counter when all beasts are found.")]
    public ColorNode TextColor { get; set; } = new(new Color(90, 255, 120, 255));

    [Menu("Border Color", "Border color for the main counter when all beasts are found.")]
    public ColorNode BorderColor { get; set; } = new(new Color(90, 255, 120, 255));
}

[Submenu(CollapsedByDefault = false)]
public class CompletedMessageWindowSettings
{
    [Menu("Show", "Show or hide the separate completed message window.")]
    public ToggleNode Show { get; set; } = new(true);

    [Menu("Show While Not Complete", "Testing mode: show completed message even before all beasts are found.")]
    public ToggleNode ShowWhileNotComplete { get; set; } = new(false);

    [Menu("Message Text", "Custom text shown in the completed message window.")]
    public TextNode Text { get; set; } = new("All beasts found!");

    [Menu("X Position (%)", "Horizontal position of the completed message window.")]
    public RangeNode<float> XPos { get; set; } = new(50, 0, 100);

    [Menu("Y Position (%)", "Vertical position of the completed message window.")]
    public RangeNode<float> YPos { get; set; } = new(16, 0, 100);

    [Menu("Padding", "Inner spacing between completed message text and window border.")]
    public RangeNode<float> Padding { get; set; } = new(8, 0, 50);

    [Menu("Border Thickness", "Border thickness of the completed message window.")]
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 10);

    [Menu("Border Rounding", "Corner roundness of the completed message window.")]
    public RangeNode<float> BorderRounding { get; set; } = new(4, 0, 25);

    [Menu("Text Scale", "Text scale of the completed message text.")]
    public RangeNode<float> TextScale { get; set; } = new(1.4f, 0.5f, 6f);

    [Menu("Text Color", "Text color of the completed message window.")]
    public ColorNode TextColor { get; set; } = new(new Color(120, 255, 140, 255));

    [Menu("Border Color", "Border color of the completed message window.")]
    public ColorNode BorderColor { get; set; } = new(new Color(90, 255, 120, 255));

    [Menu("Background Color", "Background color of the completed message window.")]
    public ColorNode BackgroundColor { get; set; } = new(new Color(0, 0, 0, 200));
}

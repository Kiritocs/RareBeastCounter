using System.Collections.Generic;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using SharpDX;

namespace RareBeastCounter;

public class RareBeastCounterSettings : ISettings
{
    [Menu("Enable", "Enable or disable the Rare Beast Counter plugin.")]
    public ToggleNode Enable { get; set; } = new(false);

    [Menu("Counter Overlay", "Main beast counter overlay settings.")]
    public CounterWindowSettings CounterWindow { get; set; } = new();

    [Menu("Analytics Overlay", "Session and map timing analytics, including valuable beast counts.")]
    public AnalyticsWindowSettings AnalyticsWindow { get; set; } = new();

    [Menu("Visibility Rules", "Rules for hiding the counter and analytics overlays in specific UI states.")]
    public VisibilitySettings Visibility { get; set; } = new();

    [Menu("Beast Markers & Panel Overlays", "Settings for world labels, map labels, tracked beasts, and UI price overlays.")]
    public MapRenderSettings MapRender { get; set; } = new();

    [Menu("Price Data", "Settings for fetching and using beast prices from poe.ninja.")]
    public BeastPricesSettings BeastPrices { get; set; } = new();

    [Menu("Bestiary Clipboard", "Automatically copy a Bestiary search regex when the Bestiary tab is opened.")]
    public BestiaryClipboardSettings BestiaryClipboard { get; set; } = new();
}

[Submenu(CollapsedByDefault = false)]
public class VisibilitySettings
{
    [Menu("Hide Counter & Message In Hideout", "Hide the main counter and completed message while inside a hideout.")]
    public ToggleNode HideInHideout { get; set; } = new(true);

    [Menu("Hide Counter & Message On Fullscreen Panels", "Hide the main counter and completed message while any fullscreen panel is open.")]
    public ToggleNode HideOnFullscreenPanels { get; set; } = new(true);

    [Menu("Hide Counter & Message On Open Left Panel", "Hide the main counter and completed message while the left side panel is open.")]
    public ToggleNode HideOnOpenLeftPanel { get; set; } = new(true);

    [Menu("Hide Counter & Message On Open Right Panel", "Hide the main counter and completed message while the right side panel is open.")]
    public ToggleNode HideOnOpenRightPanel { get; set; } = new(true);

    [Menu("Hide Analytics On Open Left Panel", "Hide the analytics overlay while the left side panel is open.")]
    public ToggleNode HideAnalyticsOnOpenLeftPanel { get; set; } = new(true);

    [Menu("Hide Analytics On Open Right Panel", "Hide the analytics overlay while the right side panel is open.")]
    public ToggleNode HideAnalyticsOnOpenRightPanel { get; set; } = new(true);
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

    [Menu("Completed Counter Style", "How the main counter looks after all beasts in the area are found.")]
    public CompletedCounterSettings CompletedStyle { get; set; } = new();

    [Menu("Completed Message Overlay", "Separate message overlay shown after all beasts in the area are found.")]
    public CompletedMessageWindowSettings CompletedMessage { get; set; } = new();
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

[Submenu(CollapsedByDefault = false)]
public class AnalyticsWindowSettings
{
    [Menu("Show", "Show or hide the analytics window.")]
    public ToggleNode Show { get; set; } = new(true);

    [Menu("Reset Session", "Reset current analytics session counters and timers (hold Shift while pressing).")]
    public ButtonNode ResetSession { get; set; } = new();

    [Menu("Reset Map Average", "Reset only the completed-map average counters (map count and total duration). Hold Shift while pressing.")]
    public ButtonNode ResetMapAverage { get; set; } = new();

    [Menu("Save Session To File", "Save the current session snapshot to a CSV file.")]
    public ButtonNode SaveSessionToFile { get; set; } = new();

    [Menu("X Position (%)", "Horizontal position of the analytics window.")]
    public RangeNode<float> XPos { get; set; } = new(50, 0, 100);

    [Menu("Y Position (%)", "Vertical position of the analytics window.")]
    public RangeNode<float> YPos { get; set; } = new(25, 0, 100);

    [Menu("Padding", "Inner spacing between analytics text and window border.")]
    public RangeNode<float> Padding { get; set; } = new(8, 0, 50);

    [Menu("Border Thickness", "Border thickness of the analytics window.")]
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 10);

    [Menu("Border Rounding", "Corner roundness of the analytics window.")]
    public RangeNode<float> BorderRounding { get; set; } = new(0, 0, 25);

    [Menu("Text Scale", "Text scale of the analytics window.")]
    public RangeNode<float> TextScale { get; set; } = new(1.0f, 0.5f, 6f);

    [Menu("Text Color", "Text color of analytics lines.")]
    public ColorNode TextColor { get; set; } = new(new Color(220, 220, 220, 255));

    [Menu("Border Color", "Border color of the analytics window.")]
    public ColorNode BorderColor { get; set; } = new(new Color(90, 90, 90, 255));

    [Menu("Background Color", "Background color of the analytics window.")]
    public ColorNode BackgroundColor { get; set; } = new(new Color(0, 0, 0, 180));
}

[Submenu(CollapsedByDefault = true)]
public class BeastPricesSettings
{
    [Menu("League Name", "League name used for poe.ninja price requests, for example Mirage.")]
    public TextNode League { get; set; } = new("Mirage");

    [Menu("Auto Refresh (Minutes)", "Automatically refresh beast prices every N minutes. Set to 0 to disable auto refresh.")]
    public RangeNode<int> AutoRefreshMinutes { get; set; } = new(10, 0, 60);

    [Menu("Fetch Prices", "Manually fetch current beast prices from poe.ninja.")]
    public ButtonNode FetchPrices { get; set; } = new();

    public string LastUpdated { get; set; } = "never";

    public HashSet<string> EnabledBeasts { get; set; } = new();

    [Menu("Enabled Beasts", "Choose which beasts count as enabled for filtering, analytics, and Bestiary auto-regex generation.")]
    [JsonIgnore] public CustomNode BeastPickerPanel { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class BestiaryClipboardSettings
{
    [Menu("Enable Auto Copy", "Automatically copy a Bestiary search regex when the Bestiary tab becomes visible.")]
    public ToggleNode EnableAutoCopy { get; set; } = new(true);

    [Menu("Generate Regex From Enabled Beasts", "Build the regex from Price Data -> Enabled Beasts instead of using the manual regex field.")]
    public ToggleNode UseAutoRegex { get; set; } = new(true);

    [Menu("Manual Regex", "Regex copied to the clipboard when automatic regex generation is disabled.")]
    public TextNode BeastRegex { get; set; } = new("id v|le m|ld h|s ho|k m|an fi|ul, f|cic c|nd sc|s, f|d bra|l pla|n, f|l cru| cy");
}

[Submenu(CollapsedByDefault = true)]
public class MapRenderSettings
{
    [Menu("Show World Labels", "Draw tracked beast labels and circles in the game world.")]
    public ToggleNode ShowBeastLabelsInWorld { get; set; } = new(true);

    [Menu("Show Large Map Labels", "Draw tracked beast labels on the large map.")]
    public ToggleNode ShowBeastsOnMap { get; set; } = new(true);

    [Menu("Show Tracked Beasts Window", "Show a small window listing currently tracked beasts and their prices.")]
    public ToggleNode ShowTrackedBeastsWindow { get; set; } = new(true);

    [Menu("Show Inventory Prices", "Overlay beast prices on captured beast items in the player inventory.")]
    public ToggleNode ShowPricesInInventory { get; set; } = new(true);

    [Menu("Show Stash Prices", "Overlay beast prices on captured beast items in the stash.")]
    public ToggleNode ShowPricesInStash { get; set; } = new(true);

    [Menu("Show Bestiary Prices", "Overlay beast prices in the Bestiary captured-beasts panel.")]
    public ToggleNode ShowPricesInBestiary { get; set; } = new(true);

    [Menu("Only Show Enabled Beasts", "Only show beasts that are enabled in Price Data -> Enabled Beasts.")]
    public ToggleNode ShowEnabledOnly { get; set; } = new(true);

    [Menu("Show Name Only On Map Labels", "On large-map labels, show only the beast name instead of name plus price.")]
    public ToggleNode ShowNameInsteadOfPrice { get; set; } = new(false);

    [Menu("Show Style Preview Window", "Show a movable preview window that demonstrates how the current text colors, captured text mode, and label styling look without needing a live beast.")]
    public ToggleNode ShowStylePreviewWindow { get; set; } = new(false);

    [Menu("Captured Status Text", "Controls how captured beasts are labeled in world labels and large-map labels.")]
    public CapturedTextDisplaySettings CapturedText { get; set; } = new();

    [Menu("Colors", "Colors used by world labels, large-map labels, and the tracked beasts window.")]
    public MapRenderColorSettings Colors { get; set; } = new();

    [Menu("Layout", "Size and spacing settings for world labels and circles.")]
    public MapRenderLayoutSettings Layout { get; set; } = new();

    [Menu("Experimental Exploration Route", "Experimental route and coverage overlays used for testing exploration logic.")]
    public ExplorationRouteSettings ExplorationRoute { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class CapturedTextDisplaySettings
{
    [Menu("Capture Text Only", "When enabled, captured beasts show only the capture status text. When disabled, the default mode keeps the name and price and adds a separate status line.")]
    public ToggleNode ReplaceNameAndPriceWithStatusText { get; set; } = new(false);

    [Menu("Capturing Text", "Text shown while a beast is in the first capture stage, for example Capturing.")]
    public TextNode StatusText { get; set; } = new("Capturing");

    [Menu("Captured Text", "Text shown while a beast has the second-stage captured buff and it is safe to leave the map.")]
    public TextNode CapturedStatusText { get; set; } = new("catched");

    [Menu("Capture Text Color", "Text color used for first-stage capturing text in world labels, large-map labels, and the tracked beasts window.")]
    public ColorNode CaptureTextColor { get; set; } = new(new Color(57, 255, 20, 255));

    [Menu("Captured Text Color", "Text color used for second-stage captured text when it is safe to leave the map.")]
    public ColorNode CapturedTextColor { get; set; } = new(new Color(120, 220, 255, 255));
}

[Submenu(CollapsedByDefault = true)]
public class MapRenderColorSettings
{
    [Menu("World Beast Text Color", "Name text color for normal tracked beasts in the world.")]
    public ColorNode WorldBeastColor { get; set; } = new(new Color(180, 20, 20, 255));

    [Menu("World Captured Beast Text Color", "Name text color for tracked beasts that are currently being captured or already safely captured.")]
    public ColorNode WorldCapturedBeastColor { get; set; } = new(new Color(255, 40, 40, 255));

    [Menu("World Price Text Color", "Price text color for tracked beasts in the world.")]
    public ColorNode WorldPriceTextColor { get; set; } = new(new Color(255, 235, 120, 255));

    [Menu("World Text Outline Color", "Outline color drawn behind world label text to keep it readable.")]
    public ColorNode WorldTextOutlineColor { get; set; } = new(Color.Black);

    [Menu("World Beast Circle Color", "Circle color for normal tracked beasts in the world.")]
    public ColorNode WorldBeastCircleColor { get; set; } = new(new Color(180, 20, 20, 255));

    [Menu("World Capture Circle Color", "Circle color for beasts that are currently being captured.")]
    public ColorNode WorldCaptureRingColor { get; set; } = new(Color.White);

    [Menu("World Catched Circle Color", "Circle color for beasts that already have the safe-to-leave captured buff.")]
    public ColorNode WorldCapturedCircleColor { get; set; } = new(new Color(120, 220, 255, 255));

    [Menu("Map Label Text Color", "Primary text color for large-map beast labels.")]
    public ColorNode MapMarkerTextColor { get; set; } = new(new Color(180, 20, 20, 255));

    [Menu("Map Label Background Color", "Background color behind large-map beast labels.")]
    public ColorNode MapMarkerBackgroundColor { get; set; } = new(new Color(0, 0, 0, 230));

    [Menu("Tracked Window Beast Color", "Text color for normal beasts in the tracked beasts window.")]
    public ColorNode TrackedWindowBeastColor { get; set; } = new(new Color(180, 20, 20, 255));
}

[Submenu(CollapsedByDefault = true)]
public class MapRenderLayoutSettings
{
    [Menu("World Label Line Spacing", "Vertical spacing between world label lines such as name, price, and capture text.")]
    public RangeNode<float> WorldTextLineSpacing { get; set; } = new(18f, 8f, 40f);

    [Menu("World Beast Circle Radius", "Ground circle radius for tracked beasts in the world.")]
    public RangeNode<float> WorldBeastCircleRadius { get; set; } = new(80f, 20f, 200f);

    [Menu("World Circle Outline Thickness", "Outline thickness for world beast circles.")]
    public RangeNode<float> WorldBeastCircleOutlineThickness { get; set; } = new(2f, 1f, 8f);

    [Menu("World Circle Fill Opacity (%)", "Fill opacity for world beast circles.")]
    public RangeNode<int> WorldBeastCircleFillOpacityPercent { get; set; } = new(20, 0, 100);

    [Menu("Map Label Padding X", "Horizontal padding inside large-map label backgrounds.")]
    public RangeNode<float> MapLabelPaddingX { get; set; } = new(4f, 0f, 20f);

    [Menu("Map Label Padding Y", "Vertical padding inside large-map label backgrounds.")]
    public RangeNode<float> MapLabelPaddingY { get; set; } = new(2f, 0f, 20f);

}

[Submenu(CollapsedByDefault = true)]
public class ExplorationRouteSettings
{
    [Menu("Show Route On Large Map", "Experimental overlay that draws the current exploration route on the large map.")]
    public ToggleNode ShowExplorationRoute { get; set; } = new(false);

    [Menu("Show Route Coverage On MiniMap", "Experimental overlay that draws the current exploration route and each waypoint's coverage radius on the minimap.")]
    public ToggleNode ShowCoverageOnMiniMap { get; set; } = new(false);

    [Menu("Detection Radius (Grid Units)", "Coverage radius used for exploration waypoints and the route coverage circles.")]
    public RangeNode<int> DetectionRadius { get; set; } = new(186, 20, 500);

    [Menu("Waypoint Visit Radius (Grid Units)", "Distance from the player at which a waypoint is treated as visited.")]
    public RangeNode<int> WaypointVisitRadius { get; set; } = new(35, 5, 200);

    [Menu("Show Radar Path To Next Waypoint", "Experimental overlay that draws the Radar path to the next exploration waypoint.")]
    public ToggleNode ShowPathsToBeasts { get; set; } = new(false);
}
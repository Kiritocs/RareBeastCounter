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

    [Menu("Visibility", "When the counter and completed message are allowed to render.")]
    public VisibilitySettings Visibility { get; set; } = new();

    [Menu("Counter Window", "Main on-screen counter window settings.")]
    public CounterWindowSettings CounterWindow { get; set; } = new();

    [Menu("Counter (Completed State)", "How the main counter looks when all beasts are found.")]
    public CompletedCounterSettings CompletedCounter { get; set; } = new();

    [Menu("Completed Message Window", "Separate window shown after all beasts are found.")]
    public CompletedMessageWindowSettings CompletedMessageWindow { get; set; } = new();

    [Menu("Analytics Window", "Separate window for map/session timing and valuable beast spawn analytics.")]
    public AnalyticsWindowSettings AnalyticsWindow { get; set; } = new();

    [Menu("Map & World Render", "Settings for marking beasts on map, in world, and in UI panels.")]
    public MapRenderSettings MapRender { get; set; } = new();

    [Menu("Beast Prices", "Settings for fetching beast prices from poe.ninja.")]
    public BeastPricesSettings BeastPrices { get; set; } = new();

    [Menu("Bestiary Clipboard", "Auto-copy a regex to clipboard when the Bestiary tab is opened.")]
    public BestiaryClipboardSettings BestiaryClipboard { get; set; } = new();
}

[Submenu(CollapsedByDefault = false)]
public class VisibilitySettings
{
    [Menu("Hide In Hideout", "Do not show counter/message while inside hideouts.")]
    public ToggleNode HideInHideout { get; set; } = new(true);

    [Menu("Hide On Fullscreen Panels", "Do not show overlays while any fullscreen UI panel is visible.")]
    public ToggleNode HideOnFullscreenPanels { get; set; } = new(true);

    [Menu("Hide On Open Left Panel", "Hide counter/message while OpenLeftPanel is visible.")]
    public ToggleNode HideOnOpenLeftPanel { get; set; } = new(true);

    [Menu("Hide On Open Right Panel", "Hide counter/message while OpenRightPanel is visible.")]
    public ToggleNode HideOnOpenRightPanel { get; set; } = new(true);

    [Menu("Hide Analytics On Open Left Panel", "Hide analytics overlay while OpenLeftPanel is visible.")]
    public ToggleNode HideAnalyticsOnOpenLeftPanel { get; set; } = new(true);

    [Menu("Hide Analytics On Open Right Panel", "Hide analytics overlay while OpenRightPanel is visible.")]
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
    [Menu("League", "The league name to fetch prices for (e.g. Mirage).")]
    public TextNode League { get; set; } = new("Mirage");

    [Menu("Auto-Refresh (minutes)", "Re-fetch prices automatically every N minutes. Set to 0 to disable.")]
    public RangeNode<int> AutoRefreshMinutes { get; set; } = new(10, 0, 60);

    [Menu("Fetch Prices", "Manually fetch current beast prices from poe.ninja.")]
    public ButtonNode FetchPrices { get; set; } = new();

    public string LastUpdated { get; set; } = "never";

    public HashSet<string> EnabledBeasts { get; set; } = new();

    [Menu("Beast Picker", "Select which beasts to show in the analytics window.")]
    [JsonIgnore] public CustomNode BeastPickerPanel { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class BestiaryClipboardSettings
{
    [Menu("Enable Auto-Copy", "Automatically copy the regex to clipboard when the Bestiary tab becomes visible.")]
    public ToggleNode EnableAutoCopy { get; set; } = new(true);

    [Menu("Auto-Generate Regex", "Build the regex automatically from the beasts selected in Beast Prices instead of using the manual field.")]
    public ToggleNode UseAutoRegex { get; set; } = new(true);

    [Menu("Beast Regex", "Regex copied to clipboard when Auto-Generate is off.")]
    public TextNode BeastRegex { get; set; } = new("id v|le m|ld h|s ho|k m|an fi|ul, f|cic c|nd sc|s, f|d bra|l pla|n, f|l cru| cy");
}

[Submenu(CollapsedByDefault = true)]
public class MapRenderSettings
{
    [Menu("Show Beast Labels In World", "Draw beast name and a highlight circle on entities currently detected by ExileAPI in the game world.")]
    public ToggleNode ShowBeastLabelsInWorld { get; set; } = new(true);

    [Menu("Show Beasts On Large Map", "Draw name/price markers for beasts currently detected by ExileAPI on the large map.")]
    public ToggleNode ShowBeastsOnMap { get; set; } = new(true);

    [Menu("Show Tracked Beasts Window", "Show a floating window listing beasts currently detected by ExileAPI and their prices.")]
    public ToggleNode ShowTrackedBeastsWindow { get; set; } = new(true);

    [Menu("Show Prices In Inventory", "Overlay beast prices on captured monster items in the player inventory.")]
    public ToggleNode ShowPricesInInventory { get; set; } = new(true);

    [Menu("Show Prices In Stash", "Overlay beast prices on captured monster items in the stash.")]
    public ToggleNode ShowPricesInStash { get; set; } = new(true);

    [Menu("Show Prices In Bestiary", "Overlay beast prices in the Bestiary captured-beasts panel.")]
    public ToggleNode ShowPricesInBestiary { get; set; } = new(true);

    [Menu("Show Enabled Beasts Only", "Only highlight/display beasts that are checked in the Beast Picker.")]
    public ToggleNode ShowEnabledOnly { get; set; } = new(true);

    [Menu("Show Name Instead Of Price", "Show the beast's name on map markers and inventory items instead of its chaos value.")]
    public ToggleNode ShowNameInsteadOfPrice { get; set; } = new(false);

    [Menu("⚠ EXPLORATION ROUTE (DO NOT USE)", "Broken experimental pathfinding junk. Do not enable any of these unless you are actively debugging the route code.")]
    public ExplorationRouteSettings ExplorationRoute { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class ExplorationRouteSettings
{
    [Menu("⛔ Show Exploration Route", "DO NOT USE. Broken test overlay. Draws a coverage path on the large map. Never enable this in normal play.")]
    public ToggleNode ShowExplorationRoute { get; set; } = new(false);

    [Menu("⛔ Route Detection Radius (grid units)", "DO NOT USE. Test-only value that drives waypoint spacing and the yellow radius circle.")]
    public RangeNode<int> DetectionRadius { get; set; } = new(186, 20, 500);

    [Menu("⛔ Waypoint Auto-Visit Radius (grid units)", "DO NOT USE. Test-only value — distance at which a waypoint is marked visited.")]
    public RangeNode<int> WaypointVisitRadius { get; set; } = new(35, 5, 200);

    [Menu("⛔ Show Path To Next Waypoint (Radar)", "DO NOT USE. Test-only Radar A* path to the next exploration waypoint.")]
    public ToggleNode ShowPathsToBeasts { get; set; } = new(false);
}
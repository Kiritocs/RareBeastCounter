using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    #region Diagnostics

    private void UpdateAutomationStatus(string message, bool forceLog = false)
    {
        if (!forceLog && string.Equals(_lastAutomationStatusMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastAutomationStatusMessage = message;
        LogAutomationDebug($"STATUS: {message}");
    }

    private void LogAutomationDebug(string message)
    {
        WriteAutomationLog(message, requireDebugLogging: true);
    }

    private void LogAutomationError(string message, Exception ex)
    {
        var errorMessage = ex == null
            ? message
            : $"{message} {ex.GetType().Name}: {ex.Message}";
        WriteAutomationLog($"ERROR: {errorMessage}", requireDebugLogging: false);
    }

    private void WriteAutomationLog(string message, bool requireDebugLogging)
    {
        if (requireDebugLogging && Settings?.DebugLogging?.Value != true)
        {
            return;
        }

        try
        {
            DebugWindow.LogMsg($"[RareBeastCounter.Automation] {message}");
        }
        catch
        {
        }
    }

    private static string DescribeTarget(StashAutomationTargetSettings target)
    {
        if (target == null)
        {
            return "target=null";
        }

        return $"enabled={target.Enabled.Value}, item='{target.ItemName.Value}', quantity={target.Quantity.Value}, selectedTab='{target.SelectedTabName.Value}'";
    }

    private static string DescribeStash(StashElement stash)
    {
        if (stash == null)
        {
            return "stash=null";
        }

        return $"stashVisible={stash.IsVisible}, visibleTabIndex={stash.IndexVisibleStash}, totalTabs={stash.TotalStashes}, visibleType={stash.VisibleStash?.InvType.ToString() ?? "null"}";
    }

    private static string DescribeElement(Element element)
    {
        if (element == null)
        {
            return "element=null";
        }

        var rect = element.GetClientRect();
        return $"visible={element.IsVisible}, children={element.Children?.Count ?? 0}, rect={DescribeRect(rect)}";
    }

    private static string DescribeRect(RectangleF rect)
    {
        return $"[{rect.Left:0.#},{rect.Top:0.#}] -> [{rect.Right:0.#},{rect.Bottom:0.#}]";
    }

    private static string DescribePath(IReadOnlyList<int> path)
    {
        return path == null ? "null" : string.Join("->", path);
    }

    private static string DescribePageTabs(IReadOnlyDictionary<int, Element> pageTabsByNumber)
    {
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            return "none";
        }

        return string.Join(" | ", pageTabsByNumber.OrderBy(x => x.Key).Select(x => $"{x.Key}:{DescribeElement(x.Value)}"));
    }

    private static string DescribeChildren(Element parent, int maxChildren = 12)
    {
        if (parent?.Children == null)
        {
            return "children=null";
        }

        return string.Join(" | ", parent.Children.Take(maxChildren).Select((child, index) => $"{index}:{DescribeElement(child)}"));
    }

    private static string DescribePathLookup(Element root, IReadOnlyList<int> path)
    {
        if (root == null)
        {
            return $"root=null, path={DescribePath(path)}";
        }

        if (path == null || path.Count == 0)
        {
            return $"path empty, root={DescribeElement(root)}";
        }

        var builder = new StringBuilder();
        var current = root;
        builder.Append($"root={DescribeElement(root)}");

        for (var i = 0; i < path.Count; i++)
        {
            var childIndex = path[i];
            var children = current?.Children;
            builder.Append($" -> [{childIndex}] children={children?.Count ?? 0}");

            if (children == null || childIndex < 0 || childIndex >= children.Count)
            {
                builder.Append(" (missing)");
                if (current != null)
                {
                    builder.Append($", siblings={DescribeChildren(current)}");
                }

                return builder.ToString();
            }

            current = children[childIndex];
            builder.Append($" => {DescribeElement(current)}");
        }

        if (current != null)
        {
            builder.Append($", finalChildren={DescribeChildren(current)}");
        }

        return builder.ToString();
    }

    #endregion
}

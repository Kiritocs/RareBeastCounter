using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using SharpDX;
using ImGuiNET;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    #region Map stash navigation and caching

    private const int MapStashMaxPageNumber = 6;
    private const int MapStashDiscoveryRetryCount = 3;
    private const int MapStashDiscoveryRetryDelayMs = 15;

    private async Task EnsureMapStashTierTabSelectedAsync(StashAutomationTargetSettings target)
    {
        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        var configuredMapTier = TryGetConfiguredMapTier(target);
        if (stash?.IsVisible != true || stash.VisibleStash?.InvType != InventoryType.MapStash || !configuredMapTier.HasValue)
        {
            LogAutomationDebug($"EnsureMapStashTierTabSelectedAsync skipped. {DescribeStash(stash)}, configuredTier={(configuredMapTier.HasValue ? configuredMapTier.Value : -1)}, item='{target.ItemName.Value}'");
            _lastAutomationMapStashTierSelection = -1;
            return;
        }

        var mapTier = configuredMapTier.Value;
        var tierTab = TryResolveMapStashTierTab(mapTier);
        if (tierTab == null)
        {
            LogAutomationDebug($"Map stash tier tab not found. tier={mapTier}, openLeftPanel={DescribeElement(GameController?.IngameState?.IngameUi?.OpenLeftPanel)}");
            _lastAutomationMapStashTierSelection = -1;
            return;
        }

        var selectionKey = stash.IndexVisibleStash * 100 + mapTier;
        if (_lastAutomationMapStashTierSelection == selectionKey)
        {
            LogAutomationDebug($"Map stash tier tab {mapTier} already selected for stash index {stash.IndexVisibleStash}. selectionKey={selectionKey}");
            return;
        }

        var tierRect = tierTab.GetClientRect();
        LogAutomationDebug($"Clicking map stash tier tab. tier={mapTier}, selectionKey={selectionKey}, tab={DescribeElement(tierTab)}");
        await ClickAtAsync(
            tierRect.Center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs, automation.TabSwitchDelayMs.Value));
        _lastAutomationMapStashTierSelection = selectionKey;
        LogAutomationDebug($"Map stash tier tab click complete. rememberedSelectionKey={_lastAutomationMapStashTierSelection}");
    }

    private async Task<bool> EnsureMapStashPageWithItemSelectedAsync(StashAutomationTargetSettings target, string metadata = null)
    {
        if (!IsMapStashTarget(target))
        {
            LogAutomationDebug($"EnsureMapStashPageWithItemSelectedAsync skipped because target is not a map stash target. {DescribeTarget(target)}");
            return false;
        }

        var itemName = target.ItemName.Value?.Trim();
        if (MapStashVisiblePageContainsMatch(itemName, metadata))
        {
            LogAutomationDebug($"Visible map stash page already contains requested match. item='{itemName}', metadata='{metadata}', currentPage={_lastAutomationMapStashPageNumber}");
            return true;
        }

        var pageTabsByNumber = GetMapStashPageTabsByNumber();
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            LogAutomationDebug($"No map stash page tabs found while looking for item='{itemName}', metadata='{metadata}'.");
            return false;
        }

        var orderedPageNumbers = GetMapStashSearchPageNumbers(pageTabsByNumber);
        var describedPages = orderedPageNumbers.Count > 0 ? string.Join(", ", orderedPageNumbers) : "<none>";
        LogAutomationDebug($"Searching map stash pages for item='{itemName}', metadata='{metadata}'. Pages={describedPages}, tabs={DescribePageTabs(pageTabsByNumber)}");

        foreach (var pageNumber in orderedPageNumbers)
        {
            if (!await EnsureMapStashPageSelectedAsync(target, pageNumber))
            {
                LogAutomationDebug($"Failed to select map stash page {pageNumber} while searching for item='{itemName}', metadata='{metadata}'.");
                continue;
            }

            if (MapStashVisiblePageContainsMatch(itemName, metadata))
            {
                LogAutomationDebug($"Found requested map stash item on page {pageNumber}. item='{itemName}', metadata='{metadata}'");
                return true;
            }
        }

        LogAutomationDebug($"Requested map stash item was not found on searchable pages. item='{itemName}', metadata='{metadata}'");
        return false;
    }

    private async Task<bool> EnsureMapStashPageSelectedAsync(StashAutomationTargetSettings target, int pageNumber)
    {
        var automation = Settings.StashAutomation;
        if (!IsMapStashTarget(target))
        {
            LogAutomationDebug($"EnsureMapStashPageSelectedAsync skipped because target is not a map stash target. pageNumber={pageNumber}, {DescribeTarget(target)}");
            return false;
        }

        var pageTabsPathTrace = DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageTabPath);
        if (!string.Equals(_lastAutomationMapStashPageTabsLogSignature, pageTabsPathTrace, StringComparison.Ordinal))
        {
            _lastAutomationMapStashPageTabsLogSignature = pageTabsPathTrace;
            LogAutomationDebug($"Map stash page tabs path trace: {pageTabsPathTrace}");
        }

        var pageTabsByNumber = GetMapStashPageTabsByNumber();
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            LogAutomationDebug($"EnsureMapStashPageSelectedAsync found no page tabs. requestedPage={pageNumber}");
            return false;
        }

        if (!pageTabsByNumber.TryGetValue(pageNumber, out var pageTab))
        {
            LogAutomationDebug($"Requested map stash page {pageNumber} was not found. Available pages: {string.Join(", ", pageTabsByNumber.Keys.OrderBy(x => x))}");
            return false;
        }

        var sourceIndex = GetMapStashPageSourceIndex(pageTab);
        _lastAutomationMapStashPageNumber = pageNumber;
        LogAutomationDebug($"Selecting map stash page {pageNumber}. sourceIndex={sourceIndex}, tab={DescribeElement(pageTab)}");

        await SelectMapStashPageAsync(pageTab, sourceIndex, pageNumber, automation);
        return true;
    }

    private async Task SelectMapStashPageAsync(Element pageTab, int sourceIndex, int pageNumber, StashAutomationSettings automation)
    {
        ThrowIfAutomationStopRequested();

        var timing = AutomationTiming;
        var rect = pageTab.GetClientRect();
        var center = rect.Center;
        LogAutomationDebug($"Clicking map stash page {pageNumber}. sourceIndex={sourceIndex}, rect={DescribeRect(rect)}");

        await ClickAtAsync(
            center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs, automation.TabSwitchDelayMs.Value));
        await DelayAutomationAsync(automation.TabSwitchDelayMs.Value);
    }

    private Dictionary<int, Element> GetMapStashPageTabsByNumber()
    {
        var pageTabContainer = ResolveMapStashPageTabContainer();
        if (AreCachedMapStashPageTabsByNumberValid(pageTabContainer, _lastAutomationMapStashPageTabsByNumber))
        {
            return _lastAutomationMapStashPageTabsByNumber;
        }

        var pageTabs = pageTabContainer?.Children;
        if (pageTabs == null)
        {
            _lastAutomationMapStashPageTabsByNumber = null;
            return null;
        }

        var pageTabsByNumber = new Dictionary<int, Element>();
        for (var index = 0; index < pageTabs.Count; index++)
        {
            var pageNumber = GetMapStashPageNumber(pageTabs[index]);
            if (pageNumber.HasValue && !pageTabsByNumber.ContainsKey(pageNumber.Value))
            {
                pageTabsByNumber[pageNumber.Value] = pageTabs[index];
            }
        }

        _lastAutomationMapStashPageTabContainer = pageTabContainer;
        _lastAutomationMapStashPageTabsByNumber = pageTabsByNumber;
        return _lastAutomationMapStashPageTabsByNumber;
    }

    private IReadOnlyList<int> GetMapStashSearchPageNumbers(IReadOnlyDictionary<int, Element> pageTabsByNumber)
    {
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            return Array.Empty<int>();
        }

        var orderedPageNumbers = pageTabsByNumber.Keys.ToArray();
        Array.Sort(orderedPageNumbers);
        if (!pageTabsByNumber.ContainsKey(_lastAutomationMapStashPageNumber))
        {
            return orderedPageNumbers;
        }

        var currentIndex = Array.IndexOf(orderedPageNumbers, _lastAutomationMapStashPageNumber);
        if (currentIndex < 0)
        {
            return orderedPageNumbers;
        }

        var remainingPageCount = orderedPageNumbers.Length - currentIndex - 1;
        if (remainingPageCount <= 0)
        {
            return Array.Empty<int>();
        }

        var remainingPageNumbers = new int[remainingPageCount];
        Array.Copy(orderedPageNumbers, currentIndex + 1, remainingPageNumbers, 0, remainingPageCount);
        return remainingPageNumbers;
    }

    private static int GetMapStashPageSourceIndex(Element pageTab)
    {
        return pageTab?.Parent?.Children?.IndexOf(pageTab) ?? -1;
    }

    private static int? GetMapStashPageNumber(Element pageTab)
    {
        var pageNumberElement = TryGetChildFromIndicesQuietly(pageTab, MapStashPageNumberPath);
        var pageNumberText = pageNumberElement?.GetText(16)?.Trim();
        return int.TryParse(pageNumberText, out var pageNumber) && pageNumber is >= 1 and <= MapStashMaxPageNumber
            ? pageNumber
            : null;
    }

    private static Element GetChildAtOrDefault(Element parent, int childIndex)
    {
        var children = parent?.Children;
        return children != null && childIndex >= 0 && childIndex < children.Count
            ? children[childIndex]
            : null;
    }

    private static Element TryGetChildFromIndicesQuietly(Element root, IReadOnlyList<int> path)
    {
        var current = root;
        if (current == null || path == null)
        {
            return null;
        }

        for (var i = 0; i < path.Count; i++)
        {
            var children = current.Children;
            var childIndex = path[i];
            if (children == null || childIndex < 0 || childIndex >= children.Count)
            {
                return null;
            }

            current = children[childIndex];
        }

        return current;
    }

    private static Element TryGetElementByPathQuietly(Element root, IReadOnlyList<int> path)
    {
        return TryGetChildFromIndicesQuietly(root, path);
    }

    private Element TryResolveMapStashTierTab(int mapTier)
    {
        var childIndex = mapTier <= 9 ? mapTier - 1 : mapTier - 10;
        var tierPath = mapTier <= 9 ? MapStashTierOneToNineTabPath : MapStashTierTenToSixteenTabPath;
        var openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
        InvalidateCachedMapStashUiStateIfNeeded();
        LogAutomationDebug($"Map stash tier path trace: {DescribePathLookup(openLeftPanel, tierPath)}");

        var tierContainer = TryGetElementByPathQuietly(openLeftPanel, tierPath);
        var tierTab = GetChildAtOrDefault(tierContainer, childIndex);
        if (tierTab != null)
        {
            return tierTab;
        }

        LogAutomationDebug($"Map stash tier fixed path failed. tier={mapTier}, childIndex={childIndex}, path={DescribePath(tierPath)}, container={DescribeElement(tierContainer)}, children={DescribeChildren(tierContainer)}");

        var dynamicTierGroup = ResolveMapStashTierGroupRoot(openLeftPanel);
        var dynamicTierContainer = GetChildAtOrDefault(dynamicTierGroup, mapTier <= 9 ? 0 : 1);
        var dynamicTierTabFromGroup = GetChildAtOrDefault(dynamicTierContainer, childIndex);
        if (dynamicTierTabFromGroup != null)
        {
            LogAutomationDebug($"Map stash tier dynamically resolved from tier group. tier={mapTier}, group={DescribeElement(dynamicTierGroup)}, container={DescribeElement(dynamicTierContainer)}, tab={DescribeElement(dynamicTierTabFromGroup)}");
            return dynamicTierTabFromGroup;
        }

        var tierText = mapTier.ToString();
        Element dynamicTierTab = null;
        foreach (var element in EnumerateDescendants(openLeftPanel))
        {
            if (element?.IsVisible != true)
            {
                continue;
            }

            if (string.Equals(GetElementTextRecursive(element), tierText, StringComparison.OrdinalIgnoreCase))
            {
                dynamicTierTab = element;
                break;
            }
        }

        if (dynamicTierTab != null)
        {
            LogAutomationDebug($"Map stash tier dynamically resolved. tier={mapTier}, tab={DescribeElement(dynamicTierTab)}");
        }

        return dynamicTierTab;
    }

    private Element ResolveMapStashPageTabContainer()
    {
        var openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
        InvalidateCachedMapStashUiStateIfNeeded();
        var pageTabContainer = TryGetElementByPathQuietly(openLeftPanel, MapStashPageTabPath);
        if (CountValidMapStashPageTabs(pageTabContainer) >= 6)
        {
            _lastAutomationMapStashPageTabContainer = pageTabContainer;
            TryPersistMapStashElementPath(
                openLeftPanel,
                pageTabContainer,
                hints => hints.MapStashPageTabContainerPath,
                (hints, path) => hints.MapStashPageTabContainerPath = path,
                "map stash page tab container");
            return pageTabContainer;
        }

        if (IsReusableMapStashPageTabContainer(openLeftPanel, _lastAutomationMapStashPageTabContainer))
        {
            return _lastAutomationMapStashPageTabContainer;
        }

        var persistedContainer = TryResolvePersistedMapStashElementPath(
            openLeftPanel,
            GetAutomationDynamicHints()?.MapStashPageTabContainerPath,
            element => CountValidMapStashPageTabs(element) >= 6,
            "map stash page tab container");
        if (persistedContainer != null)
        {
            _lastAutomationMapStashPageTabContainer = persistedContainer;
            return persistedContainer;
        }

        Element dynamicContainer = null;
        var bestPageCount = 0;
        var bestArea = float.MinValue;
        foreach (var element in EnumerateDescendants(openLeftPanel))
        {
            var pageCount = CountValidMapStashPageTabs(element);
            if (pageCount < 6)
            {
                continue;
            }

            var area = GetRectangleArea(element.GetClientRect());
            if (dynamicContainer == null || pageCount > bestPageCount || pageCount == bestPageCount && area > bestArea)
            {
                dynamicContainer = element;
                bestPageCount = pageCount;
                bestArea = area;
            }
        }

        _lastAutomationMapStashPageTabContainer = dynamicContainer ?? pageTabContainer;
        TryPersistMapStashElementPath(
            openLeftPanel,
            _lastAutomationMapStashPageTabContainer,
            hints => hints.MapStashPageTabContainerPath,
            (hints, path) => hints.MapStashPageTabContainerPath = path,
            "map stash page tab container");

        return _lastAutomationMapStashPageTabContainer;
    }

    private Element ResolveMapStashPageContentRoot()
    {
        var openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
        InvalidateCachedMapStashUiStateIfNeeded();
        var pageContent = TryGetElementByPathQuietly(openLeftPanel, MapStashPageContentPath);
        if (TryRememberMapStashPageContentRoot(openLeftPanel, pageContent, "fixed path"))
        {
            return pageContent;
        }

        if (IsReusableMapStashPageContentRoot(_lastAutomationMapStashPageContentRoot))
        {
            return _lastAutomationMapStashPageContentRoot;
        }

        var persistedContentRoot = TryResolvePersistedMapStashElementPath(
            openLeftPanel,
            GetAutomationDynamicHints()?.MapStashPageContentRootPath,
            IsReusableMapStashPageContentRoot,
            "map stash page content root");
        if (persistedContentRoot != null)
        {
            if (TryRememberMapStashPageContentRoot(openLeftPanel, persistedContentRoot, "persisted path"))
            {
                return persistedContentRoot;
            }

            _lastAutomationMapStashPageContentRoot = null;
        }

        for (var attempt = 0; attempt < MapStashDiscoveryRetryCount; attempt++)
        {
            openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
            Element dynamicContent = null;
            var bestMapDescendants = 0;
            var bestArea = float.MaxValue;
            foreach (var element in EnumerateDescendants(openLeftPanel))
            {
                if (!TryGetMapStashPageContentCandidateScore(element, out var mapDescendants, out var area))
                {
                    continue;
                }

                if (dynamicContent == null || mapDescendants > bestMapDescendants || mapDescendants == bestMapDescendants && area < bestArea)
                {
                    dynamicContent = element;
                    bestMapDescendants = mapDescendants;
                    bestArea = area;
                }
            }

            if (TryRememberMapStashPageContentRoot(openLeftPanel, dynamicContent, $"dynamic attempt {attempt + 1}"))
            {
                return dynamicContent;
            }

            if (attempt < MapStashDiscoveryRetryCount - 1)
            {
                System.Threading.Thread.Sleep(MapStashDiscoveryRetryDelayMs);
            }
        }

        return IsReusableMapStashPageContentRoot(_lastAutomationMapStashPageContentRoot)
            ? _lastAutomationMapStashPageContentRoot
            : null;
    }

    private void InvalidateCachedMapStashUiStateIfNeeded()
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        var currentCacheKey = stash?.IsVisible == true && stash.VisibleStash?.InvType == InventoryType.MapStash
            ? stash.IndexVisibleStash
            : -1;

        if (_lastAutomationMapStashUiCacheKey == currentCacheKey)
        {
            return;
        }

        _lastAutomationMapStashUiCacheKey = currentCacheKey;
        _lastAutomationMapStashTierGroupRoot = null;
        _lastAutomationMapStashPageTabContainer = null;
        _lastAutomationMapStashPageTabsByNumber = null;
        _lastAutomationMapStashPageContentRoot = null;
        _lastAutomationMapStashPageContentLogSignature = null;
        _lastAutomationMapStashPageTabsLogSignature = null;
    }

    private Element ResolveMapStashTierGroupRoot(Element openLeftPanel)
    {
        if (IsMapStashTierGroupContainer(_lastAutomationMapStashTierGroupRoot))
        {
            return _lastAutomationMapStashTierGroupRoot;
        }

        var persistedTierGroup = TryResolvePersistedMapStashElementPath(
            openLeftPanel,
            GetAutomationDynamicHints()?.MapStashTierGroupPath,
            IsMapStashTierGroupContainer,
            "map stash tier group");
        if (persistedTierGroup != null)
        {
            _lastAutomationMapStashTierGroupRoot = persistedTierGroup;
            return persistedTierGroup;
        }

        Element bestTierGroup = null;
        var bestArea = float.MinValue;
        foreach (var element in EnumerateDescendants(openLeftPanel))
        {
            if (!IsMapStashTierGroupContainer(element))
            {
                continue;
            }

            var area = GetRectangleArea(element.GetClientRect());
            if (bestTierGroup == null || area > bestArea)
            {
                bestTierGroup = element;
                bestArea = area;
            }
        }

        _lastAutomationMapStashTierGroupRoot = bestTierGroup;
        TryPersistMapStashElementPath(
            openLeftPanel,
            _lastAutomationMapStashTierGroupRoot,
            hints => hints.MapStashTierGroupPath,
            (hints, path) => hints.MapStashTierGroupPath = path,
            "map stash tier group");
        return _lastAutomationMapStashTierGroupRoot;
    }

    private bool IsReusableMapStashPageTabContainer(Element root, Element element)
    {
        return IsElementAttachedToRoot(root, element) && CountValidMapStashPageTabs(element) >= 6;
    }

    private bool AreCachedMapStashPageTabsByNumberValid(Element pageTabContainer, IReadOnlyDictionary<int, Element> pageTabsByNumber)
    {
        if (pageTabContainer == null || pageTabsByNumber == null || pageTabsByNumber.Count <= 0)
        {
            return false;
        }

        foreach (var entry in pageTabsByNumber)
        {
            if (!ReferenceEquals(entry.Value?.Parent, pageTabContainer))
            {
                return false;
            }

            if ((entry.Value.Parent?.Children?.IndexOf(entry.Value) ?? -1) < 0)
            {
                return false;
            }

            if (GetMapStashPageNumber(entry.Value) != entry.Key)
            {
                return false;
            }
        }

        return true;
    }

    private StashAutomationDynamicHintSettings GetAutomationDynamicHints()
    {
        return Settings?.StashAutomation?.DynamicHints;
    }

    private Element TryResolvePersistedMapStashElementPath(
        Element root,
        IReadOnlyList<int> path,
        Func<Element, bool> validator,
        string label)
    {
        if (root == null || path == null || path.Count <= 0 || validator == null)
        {
            return null;
        }

        var resolvedElement = TryGetElementByPathQuietly(root, path);
        if (!validator(resolvedElement))
        {
            return null;
        }

        LogAutomationDebug($"Resolved {label} from persisted path {DescribePath(path)}. element={DescribeElement(resolvedElement)}");
        return resolvedElement;
    }

    private List<int> TryPersistMapStashElementPath(
        Element root,
        Element target,
        Func<StashAutomationDynamicHintSettings, List<int>> getter,
        Action<StashAutomationDynamicHintSettings, List<int>> setter,
        string label)
    {
        var hints = GetAutomationDynamicHints();
        if (root == null || target == null || hints == null || getter == null || setter == null)
        {
            return null;
        }

        var resolvedPath = TryFindPathFromRoot(root, target);
        if (resolvedPath == null || resolvedPath.Count <= 0)
        {
            return null;
        }

        var existingPath = getter(hints);
        if (existingPath != null && existingPath.SequenceEqual(resolvedPath))
        {
            LogAutomationDebug($"Persisted {label} path unchanged ({DescribePath(resolvedPath)}); skipping settings snapshot save.");
            return resolvedPath;
        }

        setter(hints, resolvedPath);
        LogAutomationDebug($"Persisted {label} path {DescribePath(resolvedPath)}");
        TrySaveSettingsSnapshot();
        return resolvedPath;
    }

    private void TrySaveSettingsSnapshot()
    {
        try
        {
            var settings = Settings;
            if (settings == null)
            {
                return;
            }

            var configDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "global");
            Directory.CreateDirectory(configDirectory);
            var settingsPath = Path.Combine(configDirectory, SettingsFileName);
            var settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(settingsPath, settingsJson);
            LogAutomationDebug($"Saved settings snapshot to '{settingsPath}'.");
        }
        catch (Exception ex)
        {
            LogAutomationDebug($"Failed to save settings snapshot: {ex.Message}");
        }
    }

    private static List<int> TryFindPathFromRoot(Element root, Element target)
    {
        if (root == null || target == null)
        {
            return null;
        }

        if (ReferenceEquals(root, target))
        {
            return [];
        }

        var stack = new Stack<(Element Element, List<int> Path)>();
        stack.Push((root, []));

        while (stack.Count > 0)
        {
            var (current, path) = stack.Pop();
            var children = current?.Children;
            if (children == null)
            {
                continue;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];
                if (child == null)
                {
                    continue;
                }

                var childPath = new List<int>(path.Count + 1);
                childPath.AddRange(path);
                childPath.Add(i);
                if (ReferenceEquals(child, target))
                {
                    return childPath;
                }

                stack.Push((child, childPath));
            }
        }

        return null;
    }

    private bool IsElementAttachedToRoot(Element root, Element target)
    {
        for (var current = target; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private Element FindFragmentScarabTabDynamically(Element root)
    {
        foreach (var element in EnumerateDescendants(root))
        {
            if (element?.IsVisible != true)
            {
                continue;
            }

            if (GetElementTextRecursive(element)?.IndexOf("Scarab", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return element;
            }
        }

        return null;
    }

    private static int CountValidMapStashPageTabs(Element element)
    {
        var children = element?.Children;
        if (children == null)
        {
            return 0;
        }

        var pageNumbers = new HashSet<int>();
        foreach (var child in children)
        {
            var pageNumber = GetMapStashPageNumber(child);
            if (pageNumber.HasValue)
            {
                pageNumbers.Add(pageNumber.Value);
            }
        }

        return pageNumbers.Count;
    }

    private static bool IsMapStashTierGroupContainer(Element element)
    {
        var children = element?.Children;
        if (children == null || children.Count < 2)
        {
            return false;
        }

        return IsMapStashTierContainer(children[0]) && IsMapStashTierContainer(children[1]);
    }

    private static bool IsMapStashTierContainer(Element element)
    {
        var children = element?.Children;
        if (children == null || children.Count < 7)
        {
            return false;
        }

        var visibleChildren = 0;
        foreach (var child in children)
        {
            if (child?.IsVisible == true)
            {
                visibleChildren++;
            }
        }

        return visibleChildren >= 7;
    }

    private static bool IsMapStashPageContentCandidate(Element element)
    {
        return TryGetMapStashPageContentCandidateScore(element, out _, out _);
    }

    private static bool TryGetMapStashPageContentCandidateScore(Element element, out int mapDescendants, out float area)
    {
        mapDescendants = 0;
        area = 0;
        if (element?.IsVisible != true || (element.Children?.Count ?? 0) <= 0)
        {
            return false;
        }

        mapDescendants = CountVisibleMapEntityDescendants(element);
        if (mapDescendants <= 0 || mapDescendants > 96)
        {
            return false;
        }

        if (CountValidMapStashPageTabs(element) >= 6 || IsMapStashTierGroupContainer(element))
        {
            return false;
        }

        foreach (var descendant in EnumerateDescendants(element))
        {
            if (CountValidMapStashPageTabs(descendant) >= 6 || IsMapStashTierGroupContainer(descendant))
            {
                return false;
            }
        }

        area = GetRectangleArea(element.GetClientRect());
        return true;
    }

    private static bool IsReusableMapStashPageContentRoot(Element element)
    {
        if (element?.IsVisible != true)
        {
            return false;
        }

        var childCount = element.Children?.Count ?? 0;
        if (childCount <= 0 || childCount > 32)
        {
            return false;
        }

        return CountValidMapStashPageTabs(element) < 6 && !IsMapStashTierGroupContainer(element);
    }

    private bool TryRememberMapStashPageContentRoot(Element root, Element element, string source)
    {
        if (!IsMapStashPageContentCandidate(element))
        {
            return false;
        }

        _lastAutomationMapStashPageContentRoot = element;
        var persistedPath = TryPersistMapStashElementPath(
            root,
            element,
            hints => hints.MapStashPageContentRootPath,
            (hints, path) => hints.MapStashPageContentRootPath = path,
            "map stash page content root");
        var logSignature = persistedPath != null
            ? DescribePath(persistedPath)
            : DescribeRect(element.GetClientRect());
        if (string.Equals(_lastAutomationMapStashPageContentLogSignature, logSignature, StringComparison.Ordinal))
        {
            return true;
        }

        _lastAutomationMapStashPageContentLogSignature = logSignature;
        if (persistedPath == null)
        {
            LogAutomationDebug($"Could not capture map stash page content root path from discovery root. source={source}, root={DescribeElement(root)}, content={DescribeElement(element)}");
        }
        LogAutomationDebug($"Map stash page content dynamically resolved via {source}. content={DescribeElement(element)}, mapDescendants={CountVisibleMapEntityDescendants(element)}, children={DescribeChildren(element)}");

        return true;
    }

    private static int CountVisibleMapEntityDescendants(Element root)
    {
        if (root == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var element in EnumerateDescendants(root, includeSelf: true))
        {
            if (element?.IsVisible == true && element.Entity?.Metadata?.IndexOf("Metadata/Items/Maps", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string GetElementTextRecursive(Element element, int maxDepth = 3)
    {
        if (element == null)
        {
            return null;
        }

        var directText = TryGetElementText(element);
        if (!string.IsNullOrWhiteSpace(directText))
        {
            return directText;
        }

        if (maxDepth <= 0 || element.Children == null)
        {
            return null;
        }

        foreach (var child in element.Children)
        {
            var text = GetElementTextRecursive(child, maxDepth - 1);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string TryGetElementText(Element element)
    {
        try
        {
            return element?.GetText(16)?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<Element> EnumerateDescendants(Element root, bool includeSelf = false)
    {
        if (root == null)
        {
            yield break;
        }

        if (includeSelf)
        {
            yield return root;
        }

        var children = root.Children;
        if (children == null)
        {
            yield break;
        }

        var stack = new Stack<Element>();
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] != null)
            {
                stack.Push(children[i]);
            }
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            var currentChildren = current.Children;
            if (currentChildren == null)
            {
                continue;
            }

            for (var i = currentChildren.Count - 1; i >= 0; i--)
            {
                if (currentChildren[i] != null)
                {
                    stack.Push(currentChildren[i]);
                }
            }
        }
    }

    private static float GetRectangleArea(RectangleF rect)
    {
        return Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
    }

    private IList<Element> GetVisibleMapStashPageItems()
    {
        for (var attempt = 0; attempt < MapStashDiscoveryRetryCount; attempt++)
        {
            var pageContent = ResolveMapStashPageContentRoot();
            if (pageContent == null)
            {
                if (attempt < MapStashDiscoveryRetryCount - 1)
                {
                    System.Threading.Thread.Sleep(MapStashDiscoveryRetryDelayMs);
                    continue;
                }

                LogAutomationDebug($"GetVisibleMapStashPageItems could not resolve page content. pathTrace={DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageContentPath)}");
                return null;
            }

            var items = new List<Element>();
            CollectVisibleEntityDescendants(pageContent, items);
            if (items.Count > 0)
            {
                return items;
            }

            if (attempt < MapStashDiscoveryRetryCount - 1)
            {
                System.Threading.Thread.Sleep(MapStashDiscoveryRetryDelayMs);
                continue;
            }

            LogAutomationDebug($"GetVisibleMapStashPageItems found no visible entity descendants in page content. content={DescribeElement(pageContent)}, children={DescribeChildren(pageContent)}");
            return null;
        }

        return null;
    }

    private static void CollectVisibleEntityDescendants(Element root, ICollection<Element> results)
    {
        if (root == null || results == null)
        {
            return;
        }

        if (root.IsVisible && root.Entity != null)
        {
            results.Add(root);
        }

        var children = root.Children;
        if (children == null)
        {
            return;
        }

        foreach (var child in children)
        {
            CollectVisibleEntityDescendants(child, results);
        }
    }

    private async Task<Element> WaitForNextMatchingMapStashPageItemAsync(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(
            timing.QuantityChangeBaseDelayMs,
            automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs + GetServerLatencyMs()));

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();

            var visiblePageItems = GetVisibleMapStashPageItems();
            var nextPageItem = FindNextMatchingMapStashPageItem(visiblePageItems, metadata);
            if (nextPageItem?.Entity != null)
            {
                LogAutomationDebug($"WaitForNextMatchingMapStashPageItemAsync found metadata='{metadata}'. item={DescribeElement(nextPageItem)}");
                return nextPageItem;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs);
        }

        LogAutomationDebug($"WaitForNextMatchingMapStashPageItemAsync timed out for metadata='{metadata}'. pathTrace={DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageContentPath)}");

        return null;
    }

    private static Element FindMapStashPageItemByName(IList<Element> items, string itemName)
    {
        if (items == null || string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        foreach (var item in items)
        {
            if (string.Equals(item?.Entity?.GetComponent<Base>()?.Name, itemName, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static Element FindNextMatchingMapStashPageItem(IList<Element> items, string metadata)
    {
        if (items == null || string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        Element bestMatch = null;
        var bestTop = float.MaxValue;
        var bestLeft = float.MaxValue;

        foreach (var item in items)
        {
            if (!string.Equals(item?.Entity?.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rect = item.GetClientRect();
            if (bestMatch != null && (rect.Top > bestTop || rect.Top == bestTop && rect.Left >= bestLeft))
            {
                continue;
            }

            bestMatch = item;
            bestTop = rect.Top;
            bestLeft = rect.Left;
        }

        return bestMatch;
    }

    private static int CountMatchingMapStashPageItems(IList<Element> items, string metadata)
    {
        if (items == null || string.IsNullOrWhiteSpace(metadata))
        {
            return 0;
        }

        var count = 0;
        foreach (var item in items)
        {
            if (string.Equals(item?.Entity?.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static string DescribeEntity(Entity entity)
    {
        if (entity == null)
        {
            return "entity=null";
        }

        return $"name='{entity.GetComponent<Base>()?.Name}', metadata='{entity.Metadata}'";
    }

    private bool MapStashVisiblePageContainsMatch(string itemName, string metadata)
    {
        var visiblePageItems = GetVisibleMapStashPageItems();
        if (visiblePageItems == null)
        {
            return false;
        }

        foreach (var child in visiblePageItems)
        {
            var entity = child?.Entity;
            if (entity == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(metadata))
            {
                if (string.Equals(entity.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(entity.GetComponent<Base>()?.Name, itemName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int GetVisibleMapStashPageMatchingQuantity(string metadata) => CountMatchingMapStashPageItems(GetVisibleMapStashPageItems(), metadata);

    private static int? TryGetConfiguredMapTier(StashAutomationTargetSettings target)
    {
        var configuredItemName = target?.ItemName.Value?.Trim();
        if (string.IsNullOrWhiteSpace(configuredItemName))
        {
            return null;
        }

        const string tierPrefix = "(Tier ";
        var tierStartIndex = configuredItemName.IndexOf(tierPrefix, StringComparison.OrdinalIgnoreCase);
        if (tierStartIndex < 0)
        {
            return null;
        }

        tierStartIndex += tierPrefix.Length;
        var tierEndIndex = configuredItemName.IndexOf(')', tierStartIndex);
        if (tierEndIndex <= tierStartIndex)
        {
            return null;
        }

        return int.TryParse(configuredItemName.Substring(tierStartIndex, tierEndIndex - tierStartIndex), out var tier) && tier is >= 1 and <= 16
            ? tier
            : null;
    }

    #endregion
}

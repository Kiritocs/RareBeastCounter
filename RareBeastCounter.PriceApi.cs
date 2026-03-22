using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using ExileCore;
using ImGuiNET;
using Newtonsoft.Json;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private static readonly Vector4 EnabledBeastTextColor = new(0.4f, 1f, 0.4f, 1f);
    private static readonly HttpClient HttpClient = new();
    private Dictionary<string, float> _beastPrices = AllRedBeasts.ToDictionary(x => x.Name, _ => -1f);
    private Dictionary<string, string> _beastPriceTexts = new(StringComparer.OrdinalIgnoreCase);
    private TrackedBeast[] _sortedBeastsByPrice = AllRedBeasts;
    private bool _isFetchingPrices;
    private DateTime _lastPriceFetchAttempt = DateTime.MinValue;

    private void DrawBeastPickerPanel()
    {
        ImGui.Text($"Prices as of: {Settings.BeastPrices.LastUpdated}");
        ImGui.Separator();

        if (!ImGui.BeginTable("##BeastPickerTable", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, 400)))
            return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 24);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var enabledBeasts = Settings.BeastPrices.EnabledBeasts;

        foreach (var beast in _sortedBeastsByPrice)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var isEnabled = enabledBeasts.Contains(beast.Name);
            if (ImGui.Checkbox($"##{beast.Name}_chk", ref isEnabled))
            {
                if (isEnabled) enabledBeasts.Add(beast.Name);
                else enabledBeasts.Remove(beast.Name);

                SavePersistedBeastPriceSettings();
            }

            ImGui.TableNextColumn();
            ImGui.Text(TryGetBeastPriceText(beast.Name, out var priceText) ? priceText : "?");

            ImGui.TableNextColumn();
            if (isEnabled)
                ImGui.TextColored(EnabledBeastTextColor, beast.Name);
            else
                ImGui.TextDisabled(beast.Name);
        }

        ImGui.EndTable();
    }

    private async Task FetchBeastPricesAsync()
    {
        if (_isFetchingPrices) return;
        _isFetchingPrices = true;
        _lastPriceFetchAttempt = DateTime.UtcNow;
        try
        {
            DebugWindow.LogMsg("[RareBeastCounter] Fetching beast prices from poe.ninja...");
            var league = Uri.EscapeDataString(Settings.BeastPrices.League.Value?.Trim() ?? "Mirage");
            var url = $"https://poe.ninja/api/data/itemoverview?league={league}&type=Beast";
            var json = await HttpClient.GetStringAsync(url);
            var response = JsonConvert.DeserializeObject<PoeNinjaBeastsResponse>(json);
            if (response?.Lines == null) return;

            var lookup = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in response.Lines)
            {
                if (line.Name != null)
                    lookup[line.Name] = line.ChaosValue;
            }

            var updated = new Dictionary<string, float>(_beastPrices);
            foreach (var tracked in AllRedBeasts)
            {
                updated[tracked.Name] = lookup.TryGetValue(tracked.Name, out var price) ? price : -1f;
            }

            _beastPrices = updated;
            RebuildPriceCaches(updated);
            Settings.BeastPrices.LastUpdated = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            SavePersistedBeastPriceSettings();
            DebugWindow.LogMsg($"[RareBeastCounter] Beast prices updated ({Settings.BeastPrices.LastUpdated}).");
        }
        catch (Exception ex)
        {
            DebugWindow.LogMsg($"[RareBeastCounter] Failed to fetch beast prices: {ex.Message}");
        }
        finally
        {
            _isFetchingPrices = false;
        }
    }

    private void RebuildPriceCaches(Dictionary<string, float> prices)
    {
        var priceTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var beast in AllRedBeasts)
        {
            if (prices.TryGetValue(beast.Name, out var price) && price >= 0)
            {
                priceTexts[beast.Name] = $"{price:0}c";
            }
        }

        _beastPriceTexts = priceTexts;
        _sortedBeastsByPrice = AllRedBeasts
            .OrderByDescending(b => prices.TryGetValue(b.Name, out var price) ? price : -1f)
            .ToArray();
    }

    private class PoeNinjaBeastsResponse
    {
        [JsonProperty("lines")] public List<PoeNinjaBeastLine> Lines { get; set; }
    }

    private class PoeNinjaBeastLine
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("chaosValue")] public float ChaosValue { get; set; }
    }
}

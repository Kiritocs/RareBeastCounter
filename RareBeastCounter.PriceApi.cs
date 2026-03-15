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
    private static readonly HttpClient HttpClient = new();
    private Dictionary<string, float> _beastPrices = AllRedBeasts.ToDictionary(x => x.Name, _ => -1f);
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

        var sorted = AllRedBeasts
            .OrderByDescending(b => _beastPrices.TryGetValue(b.Name, out var p) ? p : -1f);

        foreach (var beast in sorted)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var isEnabled = Settings.BeastPrices.EnabledBeasts.Contains(beast.Name);
            if (ImGui.Checkbox($"##{beast.Name}_chk", ref isEnabled))
            {
                if (isEnabled) Settings.BeastPrices.EnabledBeasts.Add(beast.Name);
                else Settings.BeastPrices.EnabledBeasts.Remove(beast.Name);
            }

            ImGui.TableNextColumn();
            var priceText = _beastPrices.TryGetValue(beast.Name, out var price) && price >= 0
                ? $"{price:0}c"
                : "?";
            ImGui.Text(priceText);

            ImGui.TableNextColumn();
            if (isEnabled)
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), beast.Name);
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
            Settings.BeastPrices.LastUpdated = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
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

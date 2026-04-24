using BepInEx.Configuration;
using LethalConfig;
using LethalConfig.ConfigItems;
using LethalConfig.ConfigItems.Options;

namespace LeadMeOut
{
    public static class LethalConfigHelper
    {
        public static void Register()
        {
            // --- Behavior (first) ---
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<NavigationMode>(Plugin.NavMode, new EnumDropDownOptions
            {
                Name = "Navigation Mode",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<ShowLinesPreset>(Plugin.ShowLines, new EnumDropDownOptions
            {
                Name = "Show Lines",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<RenderDistancePreset>(Plugin.RenderDistance, new EnumDropDownOptions
            {
                Name = "Render Distance",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new BoolCheckBoxConfigItem(Plugin.AutoEnableOnEntry, new BoolCheckBoxOptions
            {
                Name = "Auto-Enable On Entry",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new IntSliderConfigItem(Plugin.Brightness, new IntSliderOptions
            {
                Name = "Brightness (%)",
                Min = 20,
                Max = 100,
                RequiresRestart = false
            }));

            // --- Main Entrance ---
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<LineStyle>(Plugin.MainEntranceLineStyle, new EnumDropDownOptions
            {
                Name = "Main Entrance - Line Style",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<LineWidthPreset>(Plugin.MainEntranceLineWidth, new EnumDropDownOptions
            {
                Name = "Main Entrance - Line Width",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<LineColorPreset>(Plugin.MainEntranceColorPreset, new EnumDropDownOptions
            {
                Name = "Main Entrance - Color",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new TextInputFieldConfigItem(Plugin.MainEntranceCustomColor, new TextInputFieldOptions
            {
                Name = "Main Entrance - Custom Hex",
                RequiresRestart = false
            }));

            // --- Fire Exit ---
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<LineStyle>(Plugin.FireExitLineStyle, new EnumDropDownOptions
            {
                Name = "Fire Exit - Line Style",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<LineWidthPreset>(Plugin.FireExitLineWidth, new EnumDropDownOptions
            {
                Name = "Fire Exit - Line Width",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new EnumDropDownConfigItem<LineColorPreset>(Plugin.FireExitColorPreset, new EnumDropDownOptions
            {
                Name = "Fire Exit - Color",
                RequiresRestart = false
            }));
            LethalConfigManager.AddConfigItem(new TextInputFieldConfigItem(Plugin.FireExitCustomColor, new TextInputFieldOptions
            {
                Name = "Fire Exit - Custom Hex",
                RequiresRestart = false
            }));
        }
    }
}
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LeadMeOut
{
    public enum LineStyle { Solid, Dashed, Dotted, Arrow, Triangle, Diamond, Heart, Pawprint }
    public enum LineColorPreset { Green, Red, Cyan, Magenta, Yellow, White, Blue, Orange, Purple, Black, Custom }
    public enum LineWidthPreset { Hairline, Thin, Standard, Heavy, Thiccc }
    public enum ShowLinesPreset { ShowBoth, MainEntranceOnly, FireExitsOnly }
    public enum NavigationMode { LinearMode, CompassMode }
    public enum RenderDistancePreset { Short, Medium, Long, Full }

    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInDependency("com.rune580.LethalCompanyInputUtils", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("ainavt.lc.lethalconfig", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;
        internal static LeadMeOutInputActions InputActions;
        internal static ExitFinder ExitFinderInstance;

        // Main Entrance config
        internal static ConfigEntry<LineStyle> MainEntranceLineStyle;
        internal static ConfigEntry<LineWidthPreset> MainEntranceLineWidth;
        internal static ConfigEntry<LineColorPreset> MainEntranceColorPreset;
        internal static ConfigEntry<string> MainEntranceCustomColor;

        // Fire Exit config
        internal static ConfigEntry<LineStyle> FireExitLineStyle;
        internal static ConfigEntry<LineWidthPreset> FireExitLineWidth;
        internal static ConfigEntry<LineColorPreset> FireExitColorPreset;
        internal static ConfigEntry<string> FireExitCustomColor;

        // Behavior config
        internal static ConfigEntry<NavigationMode> NavMode;
        internal static ConfigEntry<ShowLinesPreset> ShowLines;
        internal static ConfigEntry<RenderDistancePreset> RenderDistance;
        internal static ConfigEntry<bool> AutoEnableOnEntry;
        internal static ConfigEntry<int> Brightness;

        private static GameObject runnerObject = null;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

            new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();

            // Main Entrance
            MainEntranceLineStyle = Config.Bind("Main Entrance", "LineStyle", LineStyle.Solid,
                "Line style for the main entrance path.\nStyle options do not affect Compass mode.");
            MainEntranceLineWidth = Config.Bind("Main Entrance", "LineWidth", LineWidthPreset.Standard,
                "Line width for the main entrance path.");
            MainEntranceColorPreset = Config.Bind("Main Entrance", "Color", LineColorPreset.Green,
                "Color of the main entrance line.");
            MainEntranceCustomColor = Config.Bind("Main Entrance", "CustomHex", "#00FF00",
                "Custom hex color for main entrance. Only used when Color is set to Custom.");

            // Fire Exit
            FireExitLineStyle = Config.Bind("Fire Exit", "LineStyle", LineStyle.Solid,
                "Line style for the fire exit path.\nStyle options do not affect Compass mode.");
            FireExitLineWidth = Config.Bind("Fire Exit", "LineWidth", LineWidthPreset.Standard,
                "Line width for the fire exit path.");
            FireExitColorPreset = Config.Bind("Fire Exit", "Color", LineColorPreset.Red,
                "Color of the fire exit line.");
            FireExitCustomColor = Config.Bind("Fire Exit", "CustomHex", "#FF0000",
                "Custom hex color for fire exit. Only used when Color is set to Custom.");

            // Behavior
            NavMode = Config.Bind("Behavior", "NavigationMode", NavigationMode.LinearMode,
                "LinearMode shows navigational path lines on the floor.\nCompassMode overlays directional markers on the HUD compass.\nLine Style options only affect Linear Mode and do not affect Compass Mode.");
            ShowLines = Config.Bind("Behavior", "ShowLines", ShowLinesPreset.ShowBoth,
                "Choose which exit lines to show.");
            RenderDistance = Config.Bind("Behavior", "RenderDistance", RenderDistancePreset.Medium,
                "How far ahead the path line renders. Short=15, Medium=30, Long=50, Full=unlimited.");
            AutoEnableOnEntry = Config.Bind("Behavior", "AutoEnableOnEntry", false,
                "Automatically show lines when entering a facility. Hotkey toggles navigation off/on.");
            Brightness = Config.Bind("Behavior", "Brightness", 80,
                new ConfigDescription(
                    "Brightness of exit markers (lines and compass pips). Enter a value between 20 and 100.",
                    new AcceptableValueRange<int>(20, 100)));

            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("ainavt.lc.lethalconfig"))
            {
                LethalConfigHelper.Register();
                Logger.LogInfo("LeadMeOut: LethalConfig registered.");
            }

            InputActions = new LeadMeOutInputActions();
            InputActions.Enable();

            ExitFinderInstance = new ExitFinder();

            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("LeadMeOut: Waiting for scene to create runner.");
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo($"LeadMeOut: Scene loaded - {scene.name}");
            if (runnerObject == null)
            {
                runnerObject = new GameObject("LeadMeOut_Runner");
                runnerObject.AddComponent<LeadMeOutRunner>();
                GameObject.DontDestroyOnLoad(runnerObject);
                Logger.LogInfo("LeadMeOut: Runner created.");
            }
        }

        internal static Color ResolveColor(LineColorPreset preset, string customHex, Color fallback)
        {
            switch (preset)
            {
                case LineColorPreset.Green: return new Color(0f, 1f, 0f);
                case LineColorPreset.Red: return new Color(1f, 0f, 0f);
                case LineColorPreset.Cyan: return new Color(0f, 0.94f, 1f);
                case LineColorPreset.Magenta: return new Color(1f, 0.01f, 0.74f);
                case LineColorPreset.Yellow: return new Color(0.88f, 1f, 0f);
                case LineColorPreset.White: return Color.white;
                case LineColorPreset.Blue: return new Color(0f, 0.37f, 1f);
                case LineColorPreset.Orange: return new Color(1f, 0.5f, 0f);
                case LineColorPreset.Purple: return new Color(0.51f, 0f, 1f);
                case LineColorPreset.Black: return Color.black;
                case LineColorPreset.Custom:
                    if (ColorUtility.TryParseHtmlString(customHex, out Color c)) return c;
                    return fallback;
                default: return fallback;
            }
        }

        internal static float ResolveRenderDistance(RenderDistancePreset preset)
        {
            switch (preset)
            {
                case RenderDistancePreset.Short: return 15f;
                case RenderDistancePreset.Medium: return 30f;
                case RenderDistancePreset.Long: return 50f;
                case RenderDistancePreset.Full: return float.MaxValue;
                default: return 30f;
            }
        }

        internal static float ResolveWidth(LineWidthPreset preset)
        {
            switch (preset)
            {
                case LineWidthPreset.Hairline: return 0.0125f;
                case LineWidthPreset.Thin: return 0.025f;
                case LineWidthPreset.Standard: return 0.05f;
                case LineWidthPreset.Heavy: return 0.1f;
                case LineWidthPreset.Thiccc: return 0.2f;
                default: return 0.05f;
            }
        }

        internal static float ResolvePipWidth(LineWidthPreset preset)
        {
            switch (preset)
            {
                case LineWidthPreset.Hairline: return 0.5f;
                case LineWidthPreset.Thin: return 1f;
                case LineWidthPreset.Standard: return 2f;
                case LineWidthPreset.Heavy: return 5f;
                case LineWidthPreset.Thiccc: return 8f;
                default: return 2f;
            }
        }

        internal static Color ApplyBrightness(Color c)
        {
            float b = Mathf.Clamp(Brightness.Value, 20, 100) / 100f;
            return new Color(c.r * b, c.g * b, c.b * b, c.a);
        }
    }
}
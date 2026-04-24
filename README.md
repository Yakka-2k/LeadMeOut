# LeadMeOut

**Displays customizable navigation lines on the floor or compass HUD to help you locate facility exits.**

Navigating the dark and spooky interiors of Lethal Company can be disorienting and quite lethal if you tend to get lost easily. LeadMeOut overlays real-time navigation directly into your game — either as floor-level path lines leading you to the exits, or as directional markers on your HUD compass. Two modes, fully configurable, always pointing you to safety!

---
## Note
- **Everyone in the Lobby will need to have this mod installed**

## Features

- **Two navigation modes** — Switch between Linear Mode (path lines on the floor) and Compass Mode (directional pips on the HUD compass) at any time from the config. ([LethalConfig](https://thunderstore.io/c/lethal-company/p/AinaVT/LethalConfig/) recommended for easy in-game configuration.)
- **Tracks both exits simultaneously** — Main Entrance and Fire Exits each get their own color-coded line or compass marker.
- **Locked door awareness** — If the direct path to an exit is blocked by a locked/security door, the line re-routes as far as it can, or continues through the obstacle with a pulsing warning-glow alerting you the path is blocked and may need a key or code to open.
- **Fully configurable visuals** — Choose line style, line weight, color (preset or custom hex), and brightness independently for the Main Entrance and Fire Exits.
- **Eight line styles** — Solid, Dashed, Dotted, Arrow, Triangle, Diamond, Heart, and Pawprint.
- **Render distance control** — Limit how far ahead the path renders: Short, Medium, Long, or Full.
- **Auto-enable on entry** — Optionally have navigation activate automatically whenever you enter a facility.
- **Hotkey toggle** — Enable or disable navigation on the fly with a configurable hotkey (default: `L`).
- **LethalConfig support** — Adjust all settings in-game without restarting, including a brightness slider.

---

## Screenshots

### Linear Mode — Default Lines
![Default Lines](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-DefaultLines-Final.png)
*Shows navigational lines to all exits!*

### Fire Exit Lines
![Fire Exit Line](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-FireExitLine-Final.png)
*Yes, Fire Exit navigation too!*

### Compass Mode
![Compass Mode](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-Compass-Final.png)
*Shows navigation pips down on the HUD compass.*

### Customization
![Customization 1](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-Customization1-Final.png)
*Line styles are configurable.*

![Customization 2](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-Customization2-Final.png)
*Colors are configurable.*

![Customization 3](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-Customization3-Final.png)
*You can mix and match presets!*

![Customization 4](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-Customization4-Final.png)
*Line widths are configurable.*

### Configuration
![Behavior Config](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-BehaviorConfig-Final.png)
*Behavior settings are configurable.*

![Main Entrance Config](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-MainEntranceConfig-Final.png)
*Main Entrance line settings are configurable.*

![Fire Exit Config](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-FireExitConfig-Final.png)
*Fire Exit line settings are configurable.*

---

## Installation

Install via [r2modman](https://thunderstore.io/c/lethal-company/p/ebkr/r2modman/) or the Thunderstore app. Dependencies are handled automatically.

**Manual install:** Drop `LeadMeOut.dll` into `BepInEx/plugins/LeadMeOut/`.

---

## Usage

1. Enter a facility as any player.
2. Press `L` to toggle navigation on.
3. Follow the line or compass pips to your chosen exit.
4. Press `L` again to toggle off.
5. Can be configured to auto-enable in the Behavior settings of the config and customized extensively.

The hotkey can be rebound in-game via the controls menu (requires [LethalCompanyInputUtils](https://thunderstore.io/c/lethal-company/p/Rune580/LethalCompanyInputUtils/)).

---

## Known Limitations

- **Multi-floor exits** — Fire Exits on a separate floor with no NavMesh connection will show a pulsing straight line through the ceiling/floor. The direction is correct; the pulse indicates the path isn't fully walkable.
- **Modded maps** — Navigation depends on Unity NavMesh data. Maps with incomplete or missing NavMesh bakes may produce partial paths (shown with a pulse).

---

## Credits

Developed by [Yakka_Productions](https://thunderstore.io/c/lethal-company/p/Yakka_Productions/) — [GitHub](https://github.com/Yakka-2k/LeadMeOut)
Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [LethalCompanyInputUtils](https://thunderstore.io/c/lethal-company/p/Rune580/LethalCompanyInputUtils/).

Inspired by the mod: Navigating Stars by [Nilaier](https://github.com/NilaierMusic)

---

## Changelog

### 1.0.0
- Initial release.

### 1.0.1
- Added multiplayer compatibility improvements.

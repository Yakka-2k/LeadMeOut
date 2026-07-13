# LeadMeOut
**Displays customizable navigation lines on the floor or compass HUD to help you locate facility exits.**
Navigating the dark and spooky interiors of Lethal Company can be disorienting and quite lethal if you tend to get lost easily. LeadMeOut overlays real-time navigation directly into your game — either as floor-level path lines leading you to the exits, or as directional markers on your HUD compass. Two modes, fully configurable, always pointing you to safety!
---
## Note
- **Everyone in the Lobby will need to have this mod installed**
## Features
- **Two navigation modes** — Switch between Linear Mode (path lines on the floor) and Compass Mode (directional pips on the HUD compass) at any time from the config. ([LethalConfig](https://thunderstore.io/c/lethal-company/p/AinaVT/LethalConfig/) recommended for easy in-game configuration.)
- **Tracks both exits simultaneously** — Main Entrance and Fire Exits each get their own color-coded line or compass marker.
- **Locked door awareness** — If the path to an exit is blocked by a locked door, the line re-routes to that door, marks it with a "Locked" icon, and pulses a warning-glow to tell you the way through needs a key.
- **Chained locked doors** — When a locked door is hiding *behind another locked door*, LeadMeOut works out the whole chain and points you at the first door you can actually reach. Open it, and the line advances to the next one.
- **Elevator awareness** — In the Mineshaft interior, the lines route to and from the elevator and mark it with an "Elevator" icon — no false "blocked path" warning, because the elevator is a way out, not an obstacle.
- **Dead-end awareness** — If no walkable route exists at all, the line pulses its warning-glow and shows a "Search Around" icon, telling you the way onward is somewhere nearby — possibly on a floor above or below.
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
### Indicator Icons
![Indicator Icons](https://raw.githubusercontent.com/Yakka-2k/LeadMeOut/master/Images/LeadMeOut-LineIcons-Final.png)
*Icons keep you informed!*
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
- **NavMesh gaps** — Navigation depends on Unity's NavMesh, and Lethal Company's has genuine gaps at some thresholds and stairways. Where the walkable data simply doesn't connect, the line will stop and show a "Search Around" icon rather than guess. As of 2.0.0 the mod would rather admit it's stuck than draw a confident line through a wall.
- **Multi-floor exits** — A Fire Exit on another floor with no NavMesh connection will show a pulsing line and a "Search Around" icon. The route onward may be a nearby locked door, or it may be on a floor above or below. Compass Mode is a good fallback here, since it always points in the right direction.
- **Modded maps** — Maps with incomplete or missing NavMesh bakes may produce partial paths (shown with a pulse).
---
## Credits
Developed by [Yakka_Productions](https://thunderstore.io/c/lethal-company/p/Yakka_Productions/) — [GitHub](https://github.com/Yakka-2k/LeadMeOut)
Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [LethalCompanyInputUtils](https://thunderstore.io/c/lethal-company/p/Rune580/LethalCompanyInputUtils/).
Inspired by the mod: Navigating Stars by [Nilaier](https://github.com/NilaierMusic)
---
## Changelog
### 2.0.0
- **Chained locked doors are now understood.** If the door you need is locked *behind another locked door*, LeadMeOut now maps the facility into regions separated by locked doors, finds the shortest chain of doors between you and the exit, and points you at the first one you can actually reach. Open it and the line advances to the next door in the chain. Previously each door was judged on its own, which quietly failed whenever doors were chained — including on ordinary vanilla levels.
- **Lines no longer route through walls.** The pathfinder could bridge small gaps in the NavMesh a little too eagerly, and would sometimes hop straight through a wall — or a locked door — to reach an exit on the other side, drawing a confident line the whole way. It now tells the difference between a genuine NavMesh seam and solid geometry, and refuses to cross the latter.
- **New indicator icons.** Lines now show you *why* they stopped: a **Locked** padlock on a door that needs a key, an **Elevator** icon in the Mineshaft, and a **Search Around** icon when the way onward is somewhere nearby, possibly on another floor.
- **Elevators read as exits, not obstacles.** The line to a Mineshaft elevator no longer pulses its "blocked path" warning — it runs solid to the elevator and marks it. Riding down switches navigation back to the real exit automatically.
- **Straighter doorways.** Path lines now try to be centered through door openings instead of curving across the frame.
- **Fixed Mineshaft navigation.** The line/compass now correctly routes to the bottom elevator panel instead of stopping mid-corridor when the player is on the lower level, with an offset so it aligns better with the elevator doorway. Top-level Fire Exit navigation now points to the elevator shaft as well.
  Thanks to [THORNyX](https://github.com/THORNyX) for finding the Mineshaft bug and for the help locating the bottom elevator points!
- **Performance.** Locked-door lookups are cached instead of re-scanning the whole level several times a second, and the door-region map is now built during the landing animation, so a blocked path is marked the moment you walk in.

### 1.0.1
- Added multiplayer compatibility improvements.

### 1.0.0
- Initial release.

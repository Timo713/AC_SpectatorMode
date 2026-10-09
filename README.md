# AC_SpectatorMode

Watch anyone in **Aicomi**'s overworld. The camera leaves your own character
and follows someone else around, through doors too, or flies free.

## Install

Needs BepInEx 6 (IL2CPP), as installed by the game's HF patch.

Unzip into the game folder, so that the file ends up at
`BepInEx\plugins\AC_SpectatorMode.dll`.

Works on its own. With
[AC_MainCameraExtension](https://github.com/Timo713/AC_MainCameraExtension)
installed, the watched character and the free camera also get its wheel zoom
and lens zoom.

## Keys

| Key | What it does |
|---|---|
| Page Down / Page Up | Watch the next / previous character. |
| Home | Return to your own character. |
| End | Bring your own character to the one you are watching, or to the free camera. |
| Delete | Centre the view again after dragging it. |
| Backspace | Free camera on or off. |

Every key can be changed, and has an optional second key, in the settings
(F1 in game).

## Watching someone

- The camera follows them, and through doors: the game loads the place they
  went to.
- The minimap, the game's character panel and sound follow them as well.
- Hold the right button and drag up or down to raise or lower the view, left
  or right to zoom.
- Hold both buttons and drag to slide the view sideways, or forward and back
  to move the camera nearer or further.
- A quick right click lets go of them and flies from where the camera is.
- Space (the game's first-person key) also puts the view back to normal.
- With the game's mouse pointer out (middle click), drag with the left button
  to turn the camera. If the Clothing State Menu plugin is installed, it opens
  for the watched character.

## Free camera

- Fly with W A S D, the arrow keys or the left mouse button, in the direction
  you look. E rises, Q sinks. Shift is faster, Ctrl slower.
- Right click a character to watch them, or a door to go through it.
- End puts your own character where the camera is.
- Asking to watch someone where there is nobody also starts it.

## Your own character

While the camera is away they are put away: not shown, not walking, and other
characters do not stop for them. Going back, or bringing them over, restores
them.

## Known issues

- Entering a room by camera just as the game starts a scene there: your
  character stays hidden for the scene and fades in a moment after it ends.
- The mouse pointer can blink for a frame now and then while the right button is held.

## Settings

Press F1 in game (ConfigurationManager). Everything applies at once.

| Section | Setting | What it does |
|---|---|---|
| | Enabled | Turns the whole plugin on or off. |
| General | Show Name | Shows the watched character's name in the corner. |
| General | Follow Through Doors | Off: the camera moves on to someone who is still here. |
| General | Load Scenery Around Them | Loads the part of the map they are in. |
| General | Hide My Character | Puts your own character away while watching. |
| General | Clothing Menu With Mouse Pointer | Opens Clothing State Menu for the watched character. |
| General | Bring My Character Also Returns | After End, the camera goes back to your character too. |
| Free Camera | When Nobody Is Here | Start flying when there is nobody to watch. |
| Free Camera | When They Leave Through A Door | With Follow Through Doors off: stay and fly. |
| Free Camera | Speed, Shift Speed, Ctrl Speed | How fast it flies. |
| Mouse | Tap Length | How short a right click must be to count as a tap. |
| Mouse | Drag speeds and smoothing | How fast and how gently the drags move the camera. |
| Keys | | The keys above, each with an optional second key. |

## Building

Needs the .NET 6 SDK and a copy of the game with BepInEx 6 installed and run
once, so its interop assemblies exist. Copy `Local.props.example` to
`Local.props` and point it at your game folder, or pass the path:

```bash
dotnet build AC_SpectatorMode.sln -c Release -p:InteropPath="C:\Games\Aicomi\BepInEx\interop-Aicomi\interop"
```

Copy `AC_SpectatorMode\bin\Release\net6.0\AC_SpectatorMode.dll` into the game's
`BepInEx\plugins` folder.

## Licence

MIT. See [CREDITS.md](CREDITS.md).

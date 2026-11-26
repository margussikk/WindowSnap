# WindowSnap

WindowSnap is a small Windows desktop utility that makes it easier to line up application windows. It runs in the background and watches for windows being moved or resized.

WindowSnap was made entirely with Codex using prompts; I didn’t write a single line of code.

## Features

- **Screen edge alignment:** Release a window close to a screen edge to align that edge with the monitor's usable work area. The window keeps its current size. Releasing near a corner aligns it with both adjacent edges; it does not resize the window into a quarter-screen layout.
- **Magnetic alignment:** Move a window close to a neighboring window to line up their edges. When resizing, WindowSnap can align the dragged edge with a nearby window while keeping the opposite edge in place.
- **Adjustable snap distance:** Use the settings window's slider to choose how close a window must be to a screen edge. The range is 12–80 pixels.
- **System tray controls:** Closing the settings window hides it so snapping can keep running. Open settings from the tray icon, or choose **Exit** to quit.
- **Single instance:** Starting the app again brings the existing settings window forward.

## Requirements

- Windows
- .NET 10 SDK (the project targets `net10.0-windows`)

## Build and run

From the project directory, run:

```powershell
dotnet run
```

To build without starting the app:

```powershell
dotnet build
```

## Using WindowSnap

Start WindowSnap, then move or resize a desktop window as usual. When you finish, WindowSnap aligns it to a nearby screen edge or neighboring window edge when it is within the relevant snap distance. The settings window shows the current status and lets you adjust the screen edge snap distance.

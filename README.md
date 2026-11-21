#           ❄︎ WinterIsComing ❄︎

Let it snow on your Mac! ☃️

Snow falls on your desktop and piles up on your windows and Dock. Roll it into snowballs and build a little snowman. It's all one Swift file, and every flake is drawn in code.

<!-- ![demo](docs/demo.gif) -->

## What it can do ✨

- **Cozy snowfall**: three layers of flakes (near, middle and far) drifting in a gentle wind
- **Snow piles up** on top of your windows, along the Dock and at the bottom of the screen
- **Drag a window and *whoosh*!** The snow flies off. The faster you drag, the farther it goes
- **Roll snowballs** just by moving your mouse sideways over the snow (no clicking!). They grow as they roll
- **Snowballs push each other** along, and one that's pushed off the edge falls down
- **Hold a snowball** with the left mouse button and carry it anywhere
- **Stack three snowballs** and they turn into a snowman with a carrot nose and a red scarf
- **A falling snowball goes *poof*** into a soft heap of snow, ready to be rolled again
- **Fairy lights**: colorful twinkling lights that swing into place along the top of your windows. They're off by default, so turn them on from the ❄︎ menu (or press `L`)

## How to play 🎮

- **Roll a snowball**: slide your mouse sideways over snow on a window, the Dock or the ground
- **Push it**: roll one snowball into another and it gets shoved along
- **Carry it**: hold the left mouse button on a snowball and drag
- **Build a snowman**: stack three snowballs on top of each other ⛄
- **Make it fly**: drag a window and watch the snow whoosh off
- **Hang the lights**: ❄︎ menu → **Fairy Lights** ✨
- **Forgot something?** ❄︎ menu → **How to Play**

## Download 🎁

1. Grab `WinterIsComing-mac-vX.zip` from [Releases](https://github.com/yike-shi/WINTER_IS_COMING/releases/latest) (not the "Source code" ones)
2. Unzip it and drag **WinterIsComing** into your Applications folder
3. Double-click it. The first time, macOS will say it can't open the app. Click **Done**, then go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway**. You only need to do this once ✨

> 🙏 **Why does macOS block it?** This is an early version, and it isn't signed with a paid Apple Developer account yet, so macOS doesn't recognize it. It's not a virus, promise! The app never connects to the internet, and all the code is right here for you to read. Thanks for your patience while I work on it 💙

## Build it yourself 🛠️

```sh
git clone https://github.com/yike-shi/WINTER_IS_COMING.git
cd WINTER_IS_COMING
zsh build.sh
```

Use the ❄︎ in the menu bar to pause, pick light, medium or heavy snow, switch the fairy lights on, clear the snow, or quit.

- No `swiftc`? Run `xcode-select --install` first.
- Want snow on the Dock too? Turn on **WinterIsComing** (or **Terminal**, if you run it with `build.sh`) in System Settings → Privacy & Security → Accessibility.
- Want your own `.app`? Run `zsh make_app.sh 1.0`.

## Windows version 🪟 (beta)

You asked for it, so here it is! Snow falls on your Windows desktop and piles up on your windows and the taskbar, dragging a window sends it flying — and now you can roll snowballs and build a snowman too ⛄

```sh
cd WINTER_IS_COMING/windows
dotnet run -c Release
```

**Left-click the ❄ icon** near the clock and a little panel opens with everything in it: the switch, the snow amount, the wind, snowballs, fairy lights, the snowplough and whether it starts with Windows. **Ctrl+Alt+S** stops and starts the snow from anywhere without opening anything, and right-clicking the icon still gives you the plain menu. If you can't see the ❄, it's under the **^** arrow in the tray — drag it out onto the taskbar and it stays there.

Whatever you pick there is remembered for next time. Tick **Start with Windows** in the same menu and it comes back on its own after a restart (`WinterIsComing.exe --autostart on` does the same from a script, `off` undoes it). Only the pause isn't remembered — coming back to a dead desktop looks like something is broken. You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.8`).

It plays much the same way as the Mac version: **slide your mouse sideways over the snow** to roll a ball (no clicking), roll one ball into another to **push** it, **hold the left button** on a ball to carry it, and **stack three** to get a snowman with a carrot nose and a red scarf. A ball pushed off an edge falls and bursts into a heap of snow.

One difference: snowballs only roll along the **bottom** of the screen. A window's top edge holds a few pixels of snow that your cursor crosses all day, and balls left sitting up there had to be cleared by hand. Up there the same sideways sweep **brushes the snow off** instead, and it tumbles down the front of the window — as it does when you drag the window itself. The overlay only catches your clicks while the cursor is actually over a snowball, so everything else stays clickable.

**Snowplough** 🚜 — send a little truck down the bottom of the screen from the ❄ menu and it scrapes the taskbar clean, shoving a growing heap of snow (and any snowball in its way) along in front of its blade. Tick *Snowplough comes by itself* and one turns up every couple of minutes.

A note on **maximised windows**, which are far more common on Windows than on a Mac. The snow sits *above* a window's top edge, so a maximised window has no room for it and simply gets no snow line — better than a white band pinned to the edge of the screen. The windows in front of it still get theirs: because a maximised window catches every flake falling behind it, some of the flakes that fall *in front* of the windows settle on the frontmost window covering them instead. So every window you can actually see collects snow on its own top edge, and the line follows the window when you move it.

**Wind** 🌬️ — the snow always drifts in gentle gusts, and the ❄ menu has a *Wind* submenu if you want a steady breeze or a proper gale behind it, from either side. The stronger you set it, the rougher the gusts get, and it tugs at the fairy lights too.

**Fairy lights** ✨ — now on Windows as well, and on from the start: a string of small bulbs hangs itself just under the top edge of every window, drooping between its hooks, lighting up left to right, twinkling out of step with each other and swinging in the wind. They follow the windows you move, and tuck away behind anything in front. The ❄ menu switches them off again.

## Make it yours 🎨

Every setting lives at the top of `WinterIsComing.swift` (`enum Cfg`): how many flakes fall, how fast they fall, how tall the piles get, how big a snowball can grow… Change a number and run `zsh build.sh` again.

## License

[MIT](LICENSE) · Made with 💙 and a little help from Claude

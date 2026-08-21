# Supermarket Tweaks

BepInEx 5 mod for **Supermarket Together**.

## Features

- **Auto-pricing** — keeps every product at a set multiple of its market price (default 200%).
  Reprices on a new day and when a product first becomes available.
  200% is deliberate: a customer's limit is `market x Random.Range(2.01, 2.5)`, so 200% is the
  highest multiple *every* customer still accepts. Market price only ever rises (inflation is
  applied every 7 days), so a price set by hand quietly decays into a discount.
- **Game speed that survives the night** — the game forces `Time.timeScale` back to 1 at the end
  of every day, in `RpcEndDay` and `CmdEndDayFromButton`. This puts it back. It also scales the
  physics timestep the way the game's own speed control does, so 3x speed doesn't mean 3x the
  physics work per second.
- **Automatic staff roles** — pick your cashiers in the F1 panel. They work the tills while the
  shop is open and switch to restocking while it's shut, then back at opening. Separately, storage
  staff switch to restocking whenever no boxes are on the floor, and back the moment a delivery
  lands. Roles are `NPC_Info.taskPriority` (1 cashier, 2 restocker, 3 storage, 4 security,
  5 technician, 6 ordering, 7 manufacturing), changed through the game's own
  `CmdChangeEmployeePriority`.
- **Sales restart each morning** — `RpcEndDay` calls `DailySaleReset()`, which clears
  `productsIDOnSale` and `productsSaleDiscount` outright, so every day begins with no sales
  running. This remembers what was on and puts it back. An empty sale slot is a wasted roll in
  `ExtraProductsOnSaleToAdd` — the only mechanism that *adds* items to a shopping list.
- **Theft is manageable at speed** — the game has no health or damage; the only effect a hit has
  on a thief is knocking stolen goods loose in `NPC_Info.AuxiliarAnimationPlay`. Two settings make
  that survive fast-forward: drops scale with the game speed multiplier, and the anti-theft door
  alarm drops you to 1x until the thief is empty-handed or gone. Fast-forward is the one thing that
  makes theft unmanageable, because you get proportionally less real time to react.
- **Shelfless boxes are left alone** — storage staff ignore boxes whose product has no shelf row
  assigned. `MainRestockUpdate` only builds tasks from rows that already name a product
  (`if (num2 < 0) continue;`), so such a box can never be restocked; storing it just clogs the back
  room. Left on the floor it also shows you which product still needs a shelf.
- **Walk through blocking props** — `World/DisablePropCollision` turns off collision on objects
  matching a name filter (default `Elephant`), for props that physically block somewhere you want
  to reach. Colliders only, never the object itself, and purely local — nothing is networked. F11
  lists what matches so you can correct the filter without a rebuild.
- **In-game config** — F1. F5 toggles the speed boost. F10 dumps easter-egg chat phrases.

## Multiplayer

**Automatic pricing runs on the host only.** A client *can* price - `CmdUpdateProductPrice` has
`requiresAuthority: false` - which is the problem: two machines sweeping the same products on the
same triggers would send duplicate commands and fight whenever their settings differed. The host
owns it, and clients get the host's settings pushed to them so their panel reads true and the
manual "Reprice now" button uses the same numbers.

**Game speed is synced from the host.** `timeScale` is a local value, but the host's copy drives
the shared simulation while a client's drives only its own view — a host at 3x with a client at 1x
means the world moves at triple speed around a player whose own character does not. So the host
owns the clock and clients follow it; F5 on a client tells you to ask the host.

### Settings sync

One Mirror message type, ever (`SmtMessage`), carrying a `Kind` string prefixed `SMT/`. New
features become new `Kind` values inside the same envelope rather than new message types - because
an unregistered message id is fatal in this Mirror build:

    Unknown message id: {messageId} for connection: {connection}...
    -> UnpackAndInvoke returns false -> connection.Disconnect()   (exceptionsDisconnect = true)

So a second message type would disconnect every older client the first time a newer host used it.
With one envelope the id never changes and an out-of-date client silently ignores kinds and
setting keys it doesn't know. `TolerateUnknownPackets` also clears `exceptionsDisconnect` on our
own side as a second layer.

**Both players need the mod.** We can't protect a vanilla player from our packets, so the client
speaks first: a mod-vs-vanilla mismatch costs the modded player their own connection instead of
kicking someone innocent. The host only ever sends to clients that have said hello.

There is deliberately **no setting to disable syncing**. Host-side pricing, staff roles and the
alarm hold all depend on this channel, so turning it off wouldn't disable a feature — it would
silently break several, with no error on either side. If you want to join a host without the mod,
remove the plugin for that session.

## Installing

No git, no GitHub CLI, no account needed.

Download **[installer/PreLaunch.bat](installer/PreLaunch.bat)** and save it anywhere (it is the
only file you need). Then in Steam, right-click **Supermarket Together > Properties > Launch
Options** and set:

    cmd /c ""C:\path	o\PreLaunch.bat" && %command%"

Every launch now installs BepInEx if it is missing and updates the mod to the newest build first.
It fetches the installer script itself each time too, so nothing on disk goes stale.

It is deliberately best-effort: no network, GitHub unreachable, game already running - it reports
the problem and the game starts anyway.

To install or update by hand instead, run `installer/Update.bat`.

## Building

```
dotnet build SupermarketTweaks/SupermarketTweaks.csproj -c Release
```

Defaults to the local Steam install. CI has neither the game nor BepInEx, so it overrides both:

```
dotnet build SupermarketTweaks/SupermarketTweaks.csproj -c Release \
  -p:ManagedDir=refs/Managed -p:BepInExCore=bepinex/BepInEx/core
```

`refs/Managed` holds stripped reference assemblies — public metadata only, no method bodies —
regenerated by `refs/update-refs.ps1` after a game update.

# Supermarket Tweaks

Draft Nexus description, for proof-reading.

---

## Short description

Automates pricing, restocking, production runs and staff assignment.

---

## Description

Every feature is optional. F1 opens the settings panel in game, or edit the config file directly and
it reloads live.

**Pricing.** Holds every product at a set percentage of market value. Reapplies on a new day and when
you first place a product. Default 200 percent, the point customers start refusing.

**Restocking.** A Restock All button in the ordering terminal, or F8. Fills the cart with what the
shop is short of:

    target    = max(shelf capacity + 1, MinimumStock)
    shortfall = target - what you already own
    boxes     = ceil(shortfall / items per box)

Counts shelves, storage, floor boxes and anything being carried. The +1 leaves back stock. Skips
products with no shelf row.

**Manufacturing.** F9, or a button in the machine panel, queues every run your manufactured shelves
are short of, using each row's exact recipe and combinables. Runs spread across all machines rather
than piling onto one.

The recipe list shows whether each recipe has a shelf at all. Hovering a manufactured product shows
where it stands:

    Cheese Sandwich   [shelf 12/40, 25 behind]
    Fruit Salad       [NO SHELF, 50 behind]

Researched products get a marker once no locked recipe still wants them.

**Staff.** Pick your cashiers and idle staff move to where the work is.

- Cashiers and security stock once the shop is closed and empty, not when the doors shut. A customer
  who cannot find a free till turns thief.
- Storage staff stock when no boxes are on the floor.
- Technicians stock when nothing is broken and no cardboard bales are waiting.
- Order fillers stock when the packaging queue is empty. Anyone mid-order is left alone.
- Manufacturing staff stock when there is nothing to make, put away or refill.

Borrowed staff go to storage while boxes are down, restocking once clear. All return when their real
job needs doing, and their original role survives quitting.

**Speed.** F5 toggles a boost that survives the end-of-day reset. F6 is a second, faster tier. If the
anti-theft door goes off, speed drops to 1x until the thief is dealt with. Damage to thieves scales
with speed.

**Sales.** Puts yesterday's sales back each morning, or fills every slot with your most expensive
products.

**Building.** Shrinks the placement ghost's collision slightly so grid-snapped shelves stop blocking
each other. Optional switch to disable the overlap check entirely.

**Multiplayer.** Install on both. The host owns pricing, staff and sales, and sends its settings to
the client. Speed is matched on both machines. A client on an older version ignores anything it does
not recognise rather than disconnecting.

## Requirements

BepInEx 5.4.23.3 (x64)

## Installation

Extract into the game folder so the DLL lands in `BepInEx/plugins`. Run once to generate the config.

## Notes

Settings are per machine, not saved into your world.

F10, F11 and F12 write diagnostic dumps and are disabled by default.

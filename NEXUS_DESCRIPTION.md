# Supermarket Tweaks

Draft Nexus description, for proof-reading. Plain text below so it can be pasted and then marked up
with Nexus BBCode headings if you want them.

---

## Short description (the one-liner under the title)

Automates the parts of running a shop that are pure clicking: pricing, restocking, production runs,
and moving idle staff to wherever the work actually is.

---

## Description

Supermarket Together asks you to repeat a lot of decisions you have already made. Prices drift out
of line every time inflation moves. Restocking means reading a shelf, doing arithmetic, and ordering
box by box. Half your staff stand still while the other half fall behind.

This mod does those jobs for you and leaves the interesting ones alone.

Everything is optional and configurable. Press F1 in game for the settings panel, or edit the config
file directly; it reloads while the game is running.

### Pricing

Keeps every product at a set percentage of its market value, reapplying when a new day starts and
when you place a product for the first time. Default is 200 percent, which is the point customers
start refusing to buy.

### Restocking

A "Restock All" button appears in the ordering terminal next to Buy Empty Box, and F8 does the same
thing. It fills the cart with everything the shop is short of:

    target    = max(total shelf capacity + 1, MinimumStock)
    shortfall = target - everything you already own
    boxes     = ceil(shortfall / items per box)

"Everything you already own" means shelves, storage, boxes on the floor and boxes an employee or
player is carrying, which is the same set the terminal's own stock figure counts. The +1 guarantees
something is left in the back room after the shelves are filled. Set MinimumStock higher if you want
a deeper buffer. Products with no shelf row assigned are skipped, since nobody could put them out.

### Manufacturing

F9, or a button in the machine's own panel, queues every production run your manufactured shelves
are short of. It reads each shelf row's exact recipe, combinables included, so a run always makes
something the row will accept. Runs are spread across every machine you own rather than piling onto
one, since machines produce in parallel.

The machine's recipe list is tagged with whether each recipe has a display shelf at all, and the
selected recipe shows how much you already have. Looking at a manufactured product tells you where
it stands:

    Cheese Sandwich   [shelf 12/40, 25 behind]
    Fruit Salad       [NO SHELF, 50 behind]

There is also a research tracker. The game keeps no record of what you have fed the researcher, so a
product gets a marker once you have scanned it and no locked recipe still wants it. Scanning the
same product again is not wasted, so it is only marked when there is genuinely nothing left.

### Staff

Assign your cashiers and the mod moves everyone to where the work is:

- Cashiers and security switch to stocking once the shop is closed and the last customer has left,
  not the moment the doors shut. Someone still queueing who cannot find a free till turns thief.
- Storage staff switch when there are no boxes on the floor, and switch straight back when a
  delivery lands.
- Technicians switch when nothing is broken and no cardboard bales are waiting. Hauling bales is
  their other job and the better paid one.
- Order fillers switch when the packaging queue is empty, and anyone mid-order is left alone.
- Manufacturing staff switch when there is nothing to make, nothing to put away and no manufactured
  shelf a rack could fill.

Borrowed staff go to storage while boxes are on the floor and to restocking once it is clear,
following the work rather than standing wherever they first landed. Everyone returns to their real
job the moment it needs doing, and their original role survives quitting.

### Speed

F5 toggles a speed boost that survives the end-of-day reset, which normally forces the game back to
1x. F6 is a second, faster tier so you can switch between a working speed and skipping a dead
stretch of night. If you own the anti-theft door and it goes off, speed drops to 1x until the thief
is dealt with, because that is the one event fast forward actively costs you money on.

Damage to thieves scales with game speed, so you can still recover goods when you cannot swing any
faster than a human can.

### Sales

Puts yesterday's sales back on each morning instead of the day starting with every slot empty. There
is an option to fill every slot with your most expensive products instead. Empty sale slots are
wasted rolls in the only mechanic that adds items to a customer's shopping list.

### Building

Grid-snapped shelves sometimes refuse to place because they touch by a fraction. A small tolerance
shrinks the placement ghost's collision so a shared edge stops counting, with an option to disable
the overlap check outright if that is not enough.

### Multiplayer

Both players should install it. The host owns pricing, staff roles and sales, and its settings are
sent to the client so the F1 panel tells the truth. Speed is matched on both machines, since a host
at 3x and a client at 1x means the world moves around a player whose own character does not.

Messages are tagged so a client running an older version silently ignores anything it does not
recognise rather than disconnecting.

---

## Requirements

- BepInEx 5.4.23.3 (x64)

## Installation

Extract into the game folder so the DLL lands in `BepInEx/plugins`. Run the game once to generate
the config file.

## Notes

Settings are per machine and not saved into your world, so they survive reinstalling and updating.
Everything can be turned off individually if you only want part of it.

Diagnostic dumps (F10, F11, F12) are disabled by default and only useful for reporting a problem.

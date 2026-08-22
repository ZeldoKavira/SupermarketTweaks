Supermarket Tweaks
==================

BepInEx 5 mod for Supermarket Together.


INSTALLING
----------

1. Install BepInEx 5.4.23.3 (x64) into the game folder and run the game once.
2. Extract this zip into the game folder so the DLL lands in BepInEx/plugins.
3. Run the game. The config file appears at BepInEx/config/net.zeldo.supermarkettweaks.cfg.


KEYS
----

F1   settings panel
F5   speed boost on/off
F6   super speed on/off
F8   fill the order cart with what the shop is short of
F9   queue the production runs the manufactured shelves are short of

F10, F11 and F12 write diagnostic dumps and are disabled by default. Only turn them on if you are
reporting a problem and have been asked for one.


SETTINGS
--------

Everything is optional. Use the F1 panel, or edit the config file while the game runs; it reloads
live. Sections:

  Pricing         hold products at a percentage of market value
  Ordering        the Restock All button, minimum stock level
  Manufacturing   production queueing, shelf tags, research tracker
  Staff           automatic role switching, cashier list
  Speed           the two speed tiers
  Sales           restart yesterday's sales, or discount your dearest products
  Theft           speed handling while the anti-theft alarm is going
  Building        placement overlap tolerance
  Seasonal        seasonal event triggering
  UI, World       diagnostics, off by default


MULTIPLAYER
-----------

Install on both machines. The host owns pricing, staff roles and sales; its settings are sent to the
client. Speed is matched on both. A client running an older version ignores anything it does not
recognise rather than disconnecting.


UNINSTALLING
------------

Delete BepInEx/plugins/SupermarketTweaks.dll. Settings live in BepInEx/config and are not written
into your save, so nothing in your world depends on the mod.
